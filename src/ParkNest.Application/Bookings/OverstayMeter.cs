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

        // Every session past its booked end, not just those past grace. Grace decides when a
        // *charge* lands; whether somebody else's slot is occupied is a different question and
        // asking it late is how the second renter finds out by arriving.
        var running = await _db.Bookings
            .Where(b => b.Status == BookingStatus.Active && b.ExpectedEndTime < now)
            .ToListAsync(cancellationToken);

        var billed = 0;
        var changed = false;

        foreach (var booking in running)
        {
            // One booking failing must not stop the rest: these are independent renters, and a
            // wallet that cannot be debited is exactly the case this loop exists to record.
            try
            {
                changed |= await FlagBlockedSlotAsync(booking, now, cancellationToken);

                if (booking.ExpectedEndTime < cutoff && await MeterAsync(booking, now, cancellationToken))
                {
                    billed++;
                    changed = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not meter overstay for booking {BookingId}.", booking.Id);
            }
        }

        if (changed)
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

    /// <summary>
    /// Notices that this over-run is sitting in the next renter's slot, and says so once.
    ///
    /// The credit system already bills the overstay, and that has never been the problem: the
    /// second renter is still driving to a bay that has a car in it, and nothing in the system
    /// told anybody. What is settled here is the half that does not depend on a compensation
    /// policy — that all three parties learn about it while it can still be acted on, and that the
    /// blocked renter is marked as owed a free cancellation.
    ///
    /// Flagged once and never cleared. Re-checking each tick and unflagging when the car finally
    /// moves would mean withdrawing a free cancellation from someone who has already been told
    /// they have one, and it would send the same warning every minute until they did.
    /// </summary>
    private async Task<bool> FlagBlockedSlotAsync(Booking overstaying, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_options.BlockedSlotLookaheadMinutes <= 0)
        {
            return false;
        }

        var horizon = now.AddMinutes(_options.BlockedSlotLookaheadMinutes);

        // The overlap constraint guarantees the next booking starts no earlier than this one was
        // due to end, so "the earliest one starting within the horizon" is the one being blocked.
        // Its own window must still have some life in it — a booking whose slot has been and gone
        // entirely was not blocked so much as missed, and telling that renter to hurry is absurd.
        var next = await _db.Bookings
            .Where(b => b.ParkingSpaceId == overstaying.ParkingSpaceId
                        && b.Id != overstaying.Id
                        && b.Status == BookingStatus.Held
                        && b.BlockedByBookingId == null
                        && b.StartTime <= horizon
                        && b.ExpectedEndTime > now)
            .OrderBy(b => b.StartTime)
            .FirstOrDefaultAsync(cancellationToken);

        if (next is null)
        {
            return false;
        }

        next.BlockedByBookingId = overstaying.Id;
        next.BlockedAt = now;

        _logger.LogWarning(
            "Booking {BlockedBookingId} starting {StartTime} is blocked by over-running session {BlockingBookingId} on space {SpaceId}.",
            next.Id, next.StartTime, overstaying.Id, next.ParkingSpaceId);

        await _events.PublishAsync(new NextSlotBlocked(
            next.Id, next.RenterId, overstaying.Id, overstaying.RenterId,
            next.HostId, next.ParkingSpaceId, next.StartTime),
            cancellationToken);

        return true;
    }
}
