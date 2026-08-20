using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Application.Pricing;
using ParkNest.Application.Wallets;
using ParkNest.Domain.Bookings;
using ParkNest.Domain.Common;

namespace ParkNest.Application.Bookings;

/// <summary>
/// Bills an over-run while it is happening, instead of once at checkout (PRD §10.3).
///
/// The difference is not cosmetic. Settling only at checkout means the platform is extending
/// unsecured credit for the whole over-run — a renter who parks for six hours past their slot and
/// then has no balance leaves the host short, and the only lever left is a trust-score penalty
/// against someone who has already driven away. Metering as it runs means the shortfall surfaces
/// while the car is still there and while the sum at stake is still small.
///
/// It composes with checkout by accumulating: the meter records what it has pulled in on the
/// booking, and <c>EndSessionAsync</c> charges the difference between what is owed in total and
/// what has already been taken. Neither needs to know how often the other ran.
/// </summary>
public interface IOverstayMeter
{
    /// <summary>Charges every running session that is past its window. Returns how many it billed.</summary>
    Task<int> MeterOpenSessionsAsync(CancellationToken cancellationToken = default);
}

public sealed class OverstayMeter : IOverstayMeter
{
    private readonly IParkNestDbContext _db;
    private readonly IWalletService _wallets;
    private readonly IPricingService _pricing;
    private readonly IClock _clock;
    private readonly PlatformOptions _options;
    private readonly IEventBus _events;
    private readonly ILogger<OverstayMeter> _logger;

    public OverstayMeter(
        IParkNestDbContext db,
        IWalletService wallets,
        IPricingService pricing,
        IClock clock,
        IOptions<PlatformOptions> options,
        IEventBus events,
        ILogger<OverstayMeter> logger)
    {
        _db = db;
        _wallets = wallets;
        _pricing = pricing;
        _clock = clock;
        _options = options.Value;
        _events = events;
        _logger = logger;
    }

    public async Task<int> MeterOpenSessionsAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        // Past the booked end *and* past the grace period. Grace is what stops a renter two
        // minutes late from getting a debit notification while they are walking to the car.
        var cutoff = now.AddMinutes(-_options.OverstayGraceMinutes);

        var running = await _db.Bookings
            .Where(b => b.Status == BookingStatus.Active && b.ExpectedEndTime < cutoff)
            .ToListAsync(cancellationToken);

        var billed = 0;

        foreach (var booking in running)
        {
            // One booking failing must not stop the rest: these are independent renters, and a
            // wallet that cannot be debited is exactly the case this loop exists to record.
            try
            {
                if (await MeterAsync(booking, now, cancellationToken))
                {
                    billed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not meter overstay for booking {BookingId}.", booking.Id);
            }
        }

        if (billed > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return billed;
    }

    private async Task<bool> MeterAsync(Booking booking, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Billed from the booked start, exactly as checkout does. The two must agree, or a session
        // would be priced differently depending on whether the meter happened to run.
        var occupiedMinutes = (int)Math.Ceiling((now - booking.StartTime).TotalMinutes);
        var billedMinutes = _pricing.RoundUpToIncrement(Math.Max(occupiedMinutes, 0));
        var overstayMinutes = billedMinutes - booking.BookedMinutes;

        if (overstayMinutes <= 0)
        {
            return false;
        }

        var dueSoFar = _pricing.QuoteOverstay(booking.RatePerHour, booking.OverstayMultiplier, overstayMinutes);
        var outstanding = Money.Round(dueSoFar - booking.OverstayAmount);

        if (outstanding <= 0m)
        {
            // Already collected through this increment. The meter runs more often than the billing
            // increment advances, so most ticks land here and do nothing.
            return false;
        }

        var debit = await _wallets.DebitOverstayAsync(
            booking.RenterId,
            booking.Id,
            outstanding,
            // Keyed by the increment it covers, so a tick that runs twice — two API instances, a
            // retry after a timeout — resolves to the same transaction rather than charging again.
            $"overstay:{booking.Id}:{billedMinutes}",
            cancellationToken);

        booking.OverstayAmount = Money.Round(booking.OverstayAmount + debit.Covered);

        if (debit.Shortfall > 0m)
        {
            // Recorded but not acted on: the session is still running and the car is still there,
            // so the renter can still top up. Whether this ends as a violation is settled at
            // checkout, against the final figure.
            _logger.LogWarning(
                "Booking {BookingId} is {Shortfall} short on its overstay while still running.",
                booking.Id, debit.Shortfall);
        }

        _logger.LogInformation(
            "Metered {Amount} of overstay on booking {BookingId} ({Minutes} minutes over).",
            debit.Covered, booking.Id, overstayMinutes);

        // Telling the renter while they can still act is the entire reason the meter runs at all.
        await _events.PublishAsync(new OverstayCharged(
            booking.Id, booking.RenterId, overstayMinutes, debit.Covered, debit.Shortfall),
            cancellationToken);

        return true;
    }
}
