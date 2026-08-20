using System.Security.Cryptography;
using System.Text;
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
    private readonly ICurrentUser _currentUser;
    private readonly PlatformOptions _options;
    private readonly IEventBus _events;

    public BookingService(
        IParkNestDbContext db,
        IWalletService wallets,
        IPricingService pricing,
        IClock clock,
        ICurrentUser currentUser,
        IOptions<PlatformOptions> options,
        IEventBus events)
    {
        _db = db;
        _wallets = wallets;
        _pricing = pricing;
        _clock = clock;
        _currentUser = currentUser;
        _options = options.Value;
        _events = events;
    }

    public async Task<BookingQuote> QuoteAsync(
        Guid parkingSpaceId,
        DateTimeOffset startTime,
        int durationMinutes,
        CancellationToken cancellationToken = default)
    {
        var space = await LoadBookableSpaceAsync(parkingSpaceId, cancellationToken);

        // Price against the first vehicle type the space accepts; bands rarely differ by type
        // within one space, and the renter's actual vehicle is validated at booking time.
        var vehicleType = space.SupportedVehicleTypes.Select(v => v.VehicleType).First();
        var band = await _pricing.GetBandAsync(space.City, space.Zone, vehicleType, cancellationToken);
        var quote = _pricing.QuoteBooking(space.PricePerHour, durationMinutes);

        var end = startTime.AddMinutes(quote.BilledMinutes);
        var overstayRate = Money.Round(space.PricePerHour * band.OverstayMultiplier);

        string? unavailable = null;
        try
        {
            if (startTime < _clock.UtcNow.AddMinutes(-BackdatingToleranceMinutes))
            {
                throw new DomainException("A booking cannot start in the past.");
            }

            await EnsureWithinAvailabilityAsync(space, startTime, end, cancellationToken);
            await EnsureNoOverlapAsync(space.Id, startTime, end, cancellationToken);
        }
        catch (DomainException ex)
        {
            // A quote reports the obstacle rather than throwing: the caller is asking "could I?",
            // and the reason is the useful part of the answer.
            unavailable = ex.Message;
        }

        return new BookingQuote(
            space.Id, startTime, end, quote.BilledMinutes,
            space.PricePerHour, quote.Amount, overstayRate,
            unavailable is null, unavailable);
    }

    public async Task<Booking> CreateBookingAsync(CreateBookingRequest request, CancellationToken cancellationToken = default)
    {
        // The renter is whoever holds the token. There is no way for a caller to book on someone
        // else's credits, because there is no input that says who the renter is.
        var renterId = _currentUser.RequireUserId();

        var existing = await _db.LedgerTransactions
            .FirstOrDefaultAsync(t => t.IdempotencyKey == HoldKey(request.IdempotencyKey), cancellationToken);
        if (existing?.BookingId is { } alreadyBookedId)
        {
            return await GetBookingAsync(alreadyBookedId, cancellationToken);
        }

        var space = await LoadBookableSpaceAsync(request.ParkingSpaceId, cancellationToken);

        var vehicle = await _db.Vehicles.FirstOrDefaultAsync(v => v.Id == request.VehicleId, cancellationToken)
                      ?? throw new DomainException($"Vehicle {request.VehicleId} does not exist.");

        if (vehicle.UserId != renterId)
        {
            throw new DomainException("That vehicle belongs to a different user.");
        }

        if (!space.Supports(vehicle.Type))
        {
            throw new DomainException($"This space does not accept {vehicle.Type} vehicles.");
        }

        var expectedEnd = request.StartTime.AddMinutes(request.DurationMinutes);

        if (request.StartTime < _clock.UtcNow.AddMinutes(-BackdatingToleranceMinutes))
        {
            throw new DomainException("A booking cannot start in the past.");
        }

        await EnsureWithinAvailabilityAsync(space, request.StartTime, expectedEnd, cancellationToken);
        await EnsureNoOverlapAsync(space.Id, request.StartTime, expectedEnd, cancellationToken);

        var band = await _pricing.GetBandAsync(space.City, space.Zone, vehicle.Type, cancellationToken);
        var quote = _pricing.QuoteBooking(space.PricePerHour, request.DurationMinutes);

        var booking = new Booking
        {
            ParkingSpaceId = space.Id,
            RenterId = renterId,
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
            renterId,
            booking.Id,
            quote.Amount,
            HoldKey(request.IdempotencyKey),
            cancellationToken);

        _db.Bookings.Add(booking);
        await _db.SaveChangesAsync(cancellationToken);

        // Published after the save, never before: an event is a statement that something has
        // happened, and announcing a booking that then fails to persist is a lie other modules
        // will act on.
        await _events.PublishAsync(new BookingCreated(
            booking.Id, booking.RenterId, booking.HostId, booking.ParkingSpaceId,
            booking.StartTime, booking.ExpectedEndTime, booking.HoldAmount), cancellationToken);

        return booking;
    }

    public async Task<Booking> StartSessionAsync(Guid bookingId, DetectionMethod method, DateTimeOffset? at = null, CheckInProof? proof = null, CancellationToken cancellationToken = default)
    {
        var booking = await GetOwnedBookingAsync(bookingId, cancellationToken);

        if (booking.Status != BookingStatus.Held)
        {
            throw new DomainException($"Booking {bookingId} cannot be started from status {booking.Status}.");
        }

        await VerifyDetectionAsync(booking, method, proof, cancellationToken);

        booking.ActualStartTime = at ?? _clock.UtcNow;
        booking.StartDetectionMethod = method;
        booking.Status = BookingStatus.Active;

        await _db.SaveChangesAsync(cancellationToken);

        await _events.PublishAsync(new SessionStarted(
            booking.Id, booking.RenterId, booking.HostId,
            booking.ActualStartTime.Value, booking.ExpectedEndTime, method.ToString()), cancellationToken);

        return booking;
    }

    public async Task<SessionOutcome> EndSessionAsync(Guid bookingId, DetectionMethod method, DateTimeOffset? at = null, CheckInProof? proof = null, CancellationToken cancellationToken = default)
    {
        var booking = await GetOwnedBookingAsync(bookingId, cancellationToken);

        if (booking.Status != BookingStatus.Active)
        {
            throw new DomainException($"Booking {bookingId} cannot be ended from status {booking.Status}.");
        }

        await VerifyDetectionAsync(booking, method, proof, cancellationToken);

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

            // The meter may already have taken part of this while the session ran, so only the
            // difference is charged now. Recomputing the total and subtracting — rather than
            // trusting a running tally alone — means the final figure is right whether the meter
            // ran every increment, once, or never.
            var alreadyDebited = booking.OverstayAmount;
            var outstanding = Money.Round(overstayDue - alreadyDebited);

            if (outstanding > 0m)
            {
                var debit = await _wallets.DebitOverstayAsync(
                    booking.RenterId, booking.Id, outstanding, $"overstay:{booking.Id}:final", cancellationToken);

                overstayCovered = Money.Round(alreadyDebited + debit.Covered);
            }
            else
            {
                overstayCovered = alreadyDebited;
            }

            // Measured against what was owed in total, so a renter who ran short mid-session but
            // topped up before leaving is not left carrying a violation.
            shortfall = Money.Round(overstayDue - overstayCovered);
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

        await _events.PublishAsync(new SessionEnded(
            booking.Id, booking.RenterId, booking.HostId, billedMinutes,
            settlement.GrossAmount, released, settlement.HostCredited, shortfall), cancellationToken);

        return new SessionOutcome(
            booking,
            billedMinutes,
            settlement.GrossAmount,
            released,
            settlement.HostCredited,
            settlement.PlatformFee,
            shortfall);
    }

    public async Task<CancellationTerms> PreviewCancellationAsync(
        Guid bookingId,
        CancellationToken cancellationToken = default)
    {
        var booking = await GetOwnedBookingAsync(bookingId, cancellationToken);
        return TermsFor(booking, _clock.UtcNow);
    }

    public async Task<CancellationOutcome> CancelBookingAsync(
        Guid bookingId,
        CancellationToken cancellationToken = default)
    {
        var booking = await GetOwnedBookingAsync(bookingId, cancellationToken);

        if (booking.Status != BookingStatus.Held)
        {
            throw new DomainException($"Only a booking that has not started can be cancelled (status: {booking.Status}).");
        }

        var terms = TermsFor(booking, _clock.UtcNow);

        // The unused part comes back first, so the renter's spendable balance is restored before
        // anything is charged. Settling first would briefly hold both.
        if (terms.Refund > 0m)
        {
            await _wallets.ReleaseHoldAsync(
                booking.RenterId, booking.Id, terms.Refund, $"cancel:{booking.Id}", cancellationToken);
        }

        if (terms.Fee > 0m)
        {
            // Settled exactly as a session would be, commission and all. The host lost a slot they
            // could not re-let, and reusing the settlement path means the ledger shape — and so
            // reconciliation, and the host's earnings — needs no special case for cancellations.
            var settlement = await _wallets.SettleAsync(
                new SettlementRequest(
                    booking.Id,
                    booking.RenterId,
                    booking.HostId,
                    terms.Fee,
                    $"cancel-fee:{booking.Id}"),
                cancellationToken);

            booking.SettledAmount = settlement.GrossAmount;
            booking.PlatformFee = settlement.PlatformFee;
        }

        booking.Status = BookingStatus.Cancelled;
        await _db.SaveChangesAsync(cancellationToken);

        await _events.PublishAsync(new BookingCancelled(
            booking.Id, booking.RenterId, booking.HostId, terms.Fee, terms.Refund), cancellationToken);

        return new CancellationOutcome(booking, terms.Fee, terms.Refund);
    }

    /// <summary>
    /// The cancellation charge, if any.
    ///
    /// Late is measured against the booked start rather than when the booking was made: what
    /// matters to the host is how much notice they get to re-let the slot, and a booking made a
    /// minute ago for a slot starting in five is exactly as unhelpful as one made last week.
    /// </summary>
    private CancellationTerms TermsFor(Booking booking, DateTimeOffset now)
    {
        var freeUntil = booking.StartTime.AddMinutes(-_options.FreeCancellationMinutes);
        var isFree = now < freeUntil || _options.LateCancellationFeeRate <= 0m;

        var fee = isFree
            ? 0m
            : Money.Round(booking.HoldAmount * _options.LateCancellationFeeRate);

        return new CancellationTerms(
            booking.HoldAmount,
            fee,
            Money.Round(booking.HoldAmount - fee),
            isFree,
            freeUntil);
    }

    /// <summary>
    /// Checks that the caller is entitled to claim this detection method, and that a Tier 2 claim
    /// stands up.
    ///
    /// The method is not decoration: it is the audit field a human reads when resolving a dispute
    /// about whether someone was really there. Letting a client assert any value it liked would
    /// make the strongest evidence in the system the cheapest to fabricate.
    /// </summary>
    private async Task VerifyDetectionAsync(
        Booking booking,
        DetectionMethod method,
        CheckInProof? proof,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case DetectionMethod.AppConfirmed:
                // Tier 1. The renter's word, and it says so on the booking.
                return;

            case DetectionMethod.AdminOverride:
                // Only ever the result of a human resolving a dispute.
                _currentUser.RequireAdmin();
                return;

            case DetectionMethod.AnprSensor:
                // Tier 3 arrives from hardware at the space, not from a phone. Until that exists
                // there is no honest way for a request to carry it.
                throw new DomainException("Sensor detection is not available for this space.");

            case DetectionMethod.QrGeofence:
                break;

            default:
                throw new DomainException($"{method} is not a detection method this endpoint accepts.");
        }

        if (proof is null)
        {
            throw new DomainException("Scanning the code needs the code and your location.");
        }

        var space = await _db.ParkingSpaces
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == booking.ParkingSpaceId, cancellationToken)
            ?? throw new DomainException("That space no longer exists.");

        if (string.IsNullOrEmpty(space.CheckInToken))
        {
            throw new DomainException("This space does not have a check-in code. Confirm in the app instead.");
        }

        // Fixed-time, because a comparison that returns early leaks how much of the token was
        // right and turns guessing into a per-character search.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(space.CheckInToken),
                Encoding.UTF8.GetBytes(proof.Token)))
        {
            throw new DomainException("That code does not belong to this space.");
        }

        var distance = Geo.DistanceMetres(proof.Latitude, proof.Longitude, space.Latitude, space.Longitude);

        if (distance > _options.CheckInRadiusMetres)
        {
            throw new DomainException(
                $"You appear to be {distance:0} m from the space. Move closer, or confirm in the app.");
        }
    }

    private async Task<Booking> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken) =>
        await _db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
        ?? throw new DomainException($"Booking {bookingId} does not exist.");

    /// <summary>
    /// A session may only be driven by its own renter (or an admin resolving a dispute). Without
    /// this, any authenticated user could end a stranger's session — or start one — by guessing
    /// or scraping a booking id, and the settlement would hit the wrong person's wallet.
    /// </summary>
    private async Task<Booking> GetOwnedBookingAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await GetBookingAsync(bookingId, cancellationToken);
        _currentUser.RequireSelfOrAdmin(booking.RenterId);
        return booking;
    }

    /// <summary>
    /// Loads a space with everything the booking rules need. Shared by quoting and booking so the
    /// two can never disagree about what is bookable.
    /// </summary>
    private async Task<ParkingSpace> LoadBookableSpaceAsync(Guid spaceId, CancellationToken cancellationToken)
    {
        var space = await _db.ParkingSpaces
            .Include(s => s.SupportedVehicleTypes)
            .Include(s => s.AvailabilityWindows)
            .FirstOrDefaultAsync(s => s.Id == spaceId, cancellationToken)
            ?? throw new DomainException($"Parking space {spaceId} does not exist.");

        if (space.Status != SpaceStatus.Published)
        {
            throw new DomainException("This space is not currently accepting bookings.");
        }

        if (space.SupportedVehicleTypes.Count == 0)
        {
            throw new DomainException("This space does not accept any vehicle type.");
        }

        return space;
    }

    /// <summary>
    /// Rejects a booking that falls outside the host's published hours, or inside a blackout.
    /// The windows were modelled and stored from the start but never actually checked, so a space
    /// offered 09:00–17:00 could be booked at 3am.
    /// </summary>
    private async Task EnsureWithinAvailabilityAsync(
        ParkingSpace space,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var blackedOut = await _db.AvailabilityBlackouts.AnyAsync(
            b => b.ParkingSpaceId == space.Id && b.From < end && start < b.To,
            cancellationToken);

        if (blackedOut)
        {
            throw new DomainException("The host has blocked out part of that window.");
        }

        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(space.TimeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Misconfigured listing rather than a bad request — surface it instead of silently
            // falling back to a zone that would mis-price someone's day.
            throw new DomainException($"This listing has an invalid time zone ({space.TimeZoneId}).");
        }

        // Availability is wall-clock in the space's own zone, so compare in local time.
        var localStart = TimeZoneInfo.ConvertTime(start, timeZone).DateTime;
        var localEnd = TimeZoneInfo.ConvertTime(end, timeZone).DateTime;

        var windows = space.AvailabilityWindows.ToList();

        if (!AvailabilityCalculator.IsCovered(windows, localStart, localEnd))
        {
            throw new DomainException(windows.Count == 0
                ? "This space has no published availability yet."
                : "That window falls outside the hours this space is available.");
        }
    }

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

    /// <summary>
    /// Slack for clock skew between a phone and the server, so a "book now" tap does not fail
    /// because the handset is a minute behind.
    /// </summary>
    private const int BackdatingToleranceMinutes = 5;
}
