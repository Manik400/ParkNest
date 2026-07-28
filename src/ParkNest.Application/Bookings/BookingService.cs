using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Application.Pricing;
using ParkNest.Application.Wallets;
using ParkNest.Domain.Bookings;
using ParkNest.Domain.Common;
using ParkNest.Domain.Listings;

namespace ParkNest.Application.Bookings;

/// <summary>
/// Orchestrates the booking lifecycle. It owns the <em>timing</em> rules; every credit movement
/// is delegated to <see cref="IWalletService"/> so money logic lives in exactly one place.
/// </summary>
public sealed class BookingService : IBookingService
{
    private readonly IParkNestDbContext _db;
    private readonly IWalletService _wallets;
    private readonly IPricingService _pricing;
    private readonly IClock _clock;
    private readonly PlatformOptions _options;

    public BookingService(
        IParkNestDbContext db,
        IWalletService wallets,
        IPricingService pricing,
        IClock clock,
        IOptions<PlatformOptions> options)
    {
        _db = db;
        _wallets = wallets;
        _pricing = pricing;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<Booking> CreateBookingAsync(CreateBookingRequest request, CancellationToken cancellationToken = default)
    {
        var existing = await _db.LedgerTransactions
            .FirstOrDefaultAsync(t => t.IdempotencyKey == HoldKey(request.IdempotencyKey), cancellationToken);
        if (existing?.BookingId is { } alreadyBookedId)
        {
            return await GetBookingAsync(alreadyBookedId, cancellationToken);
        }

        var space = await _db.ParkingSpaces
            .Include(s => s.SupportedVehicleTypes)
            .Include(s => s.AvailabilityWindows)
            .FirstOrDefaultAsync(s => s.Id == request.ParkingSpaceId, cancellationToken)
            ?? throw new DomainException($"Parking space {request.ParkingSpaceId} does not exist.");

        if (space.Status != SpaceStatus.Published)
        {
            throw new DomainException("This space is not currently accepting bookings.");
        }

        var vehicle = await _db.Vehicles.FirstOrDefaultAsync(v => v.Id == request.VehicleId, cancellationToken)
                      ?? throw new DomainException($"Vehicle {request.VehicleId} does not exist.");

        if (vehicle.UserId != request.RenterId)
        {
            throw new DomainException("That vehicle belongs to a different user.");
        }

        if (!space.Supports(vehicle.Type))
        {
            throw new DomainException($"This space does not accept {vehicle.Type} vehicles.");
        }

        var expectedEnd = request.StartTime.AddMinutes(request.DurationMinutes);
        await EnsureNoOverlapAsync(space.Id, request.StartTime, expectedEnd, cancellationToken);

        var band = await _pricing.GetBandAsync(space.City, space.Zone, vehicle.Type, cancellationToken);
        var quote = _pricing.QuoteBooking(space.PricePerHour, request.DurationMinutes);

        var booking = new Booking
        {
            ParkingSpaceId = space.Id,
            RenterId = request.RenterId,
            HostId = space.HostId,
            VehicleId = vehicle.Id,
            StartTime = request.StartTime,
            ExpectedEndTime = request.StartTime.AddMinutes(quote.BilledMinutes),
            RatePerHour = space.PricePerHour,
            OverstayMultiplier = band.OverstayMultiplier,
            BookedMinutes = quote.BilledMinutes,
            HoldAmount = quote.Amount,
            Status = BookingStatus.Held,
            CreatedAt = _clock.UtcNow
        };

        // Reserve first: if the renter is short, no booking should exist at all (PRD §5.1.1).
        await _wallets.PlaceHoldAsync(
            request.RenterId,
            booking.Id,
            quote.Amount,
            HoldKey(request.IdempotencyKey),
            cancellationToken);

        _db.Bookings.Add(booking);
        await _db.SaveChangesAsync(cancellationToken);

        return booking;
    }

    public async Task<Booking> StartSessionAsync(Guid bookingId, DetectionMethod method, DateTimeOffset? at = null, CancellationToken cancellationToken = default)
    {
        var booking = await GetBookingAsync(bookingId, cancellationToken);

        if (booking.Status != BookingStatus.Held)
        {
            throw new DomainException($"Booking {bookingId} cannot be started from status {booking.Status}.");
        }

        booking.ActualStartTime = at ?? _clock.UtcNow;
        booking.StartDetectionMethod = method;
        booking.Status = BookingStatus.Active;

        await _db.SaveChangesAsync(cancellationToken);
        return booking;
    }

    public async Task<SessionOutcome> EndSessionAsync(Guid bookingId, DetectionMethod method, DateTimeOffset? at = null, CancellationToken cancellationToken = default)
    {
        var booking = await GetBookingAsync(bookingId, cancellationToken);

        if (booking.Status != BookingStatus.Active)
        {
            throw new DomainException($"Booking {bookingId} cannot be ended from status {booking.Status}.");
        }

        var endedAt = at ?? _clock.UtcNow;
        var startedAt = booking.ActualStartTime ?? booking.StartTime;
        if (endedAt < startedAt)
        {
            throw new DomainException("Session end time cannot precede its start time.");
        }

        // Billing runs from the *booked* start, not the check-in: arriving late does not shorten
        // the window the host held open, and the space was unavailable to anyone else regardless.
        var occupiedMinutes = (int)Math.Ceiling((endedAt - booking.StartTime).TotalMinutes);
        var billedMinutes = Math.Max(
            _options.BillingIncrementMinutes,
            _pricing.RoundUpToIncrement(Math.Max(occupiedMinutes, 0)));

        decimal released = 0m;
        decimal overstayCovered = 0m;
        decimal shortfall = 0m;
        decimal amountFromHold;

        if (billedMinutes <= booking.BookedMinutes)
        {
            // Ended early — bill the time used and hand the rest back.
            amountFromHold = Money.Round(booking.RatePerHour * billedMinutes / 60m);
            amountFromHold = Math.Min(amountFromHold, booking.HoldAmount);
            released = Money.Round(booking.HoldAmount - amountFromHold);

            if (released > 0m)
            {
                await _wallets.ReleaseHoldAsync(
                    booking.RenterId, booking.Id, released, $"release:{booking.Id}", cancellationToken);
            }
        }
        else
        {
            // Overstay — pull the extra from spendable into the hold, then settle the whole hold.
            var overstayMinutes = billedMinutes - booking.BookedMinutes;
            var overstayDue = _pricing.QuoteOverstay(booking.RatePerHour, booking.OverstayMultiplier, overstayMinutes);

            var debit = await _wallets.DebitOverstayAsync(
                booking.RenterId, booking.Id, overstayDue, $"overstay:{booking.Id}", cancellationToken);

            overstayCovered = debit.Covered;
            shortfall = debit.Shortfall;
            amountFromHold = Money.Round(booking.HoldAmount + overstayCovered);
        }

        var settlement = await _wallets.SettleAsync(
            new SettlementRequest(booking.Id, booking.RenterId, booking.HostId, amountFromHold, $"settle:{booking.Id}"),
            cancellationToken);

        booking.ActualEndTime = endedAt;
        booking.EndDetectionMethod = method;
        booking.BilledMinutes = billedMinutes;
        booking.OverstayAmount = overstayCovered;
        booking.SettledAmount = settlement.GrossAmount;
        booking.PlatformFee = settlement.PlatformFee;
        booking.ShortfallAmount = shortfall;

        // An uncovered overstay restricts future access rather than chasing cash (PRD §5.1.4).
        booking.Status = shortfall > 0m ? BookingStatus.InViolation : BookingStatus.Completed;

        if (shortfall > 0m)
        {
            var renter = await _db.Users.FirstAsync(u => u.Id == booking.RenterId, cancellationToken);
            renter.TrustScore = Math.Max(0, renter.TrustScore - 10);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return new SessionOutcome(
            booking,
            billedMinutes,
            settlement.GrossAmount,
            released,
            settlement.HostCredited,
            settlement.PlatformFee,
            shortfall);
    }

    public async Task<Booking> CancelBookingAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var booking = await GetBookingAsync(bookingId, cancellationToken);

        if (booking.Status != BookingStatus.Held)
        {
            throw new DomainException($"Only a booking that has not started can be cancelled (status: {booking.Status}).");
        }

        await _wallets.ReleaseHoldAsync(
            booking.RenterId, booking.Id, booking.HoldAmount, $"cancel:{booking.Id}", cancellationToken);

        booking.Status = BookingStatus.Cancelled;
        await _db.SaveChangesAsync(cancellationToken);

        return booking;
    }

    private async Task<Booking> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken) =>
        await _db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
        ?? throw new DomainException($"Booking {bookingId} does not exist.");

    private async Task EnsureNoOverlapAsync(Guid spaceId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var overlaps = await _db.Bookings.AnyAsync(
            b => b.ParkingSpaceId == spaceId
                 && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Active)
                 && b.StartTime < end
                 && start < b.ExpectedEndTime,
            cancellationToken);

        if (overlaps)
        {
            throw new DomainException("That space is already booked for part of the requested window.");
        }
    }

    private static string HoldKey(string idempotencyKey) => $"hold:{idempotencyKey}";
}
