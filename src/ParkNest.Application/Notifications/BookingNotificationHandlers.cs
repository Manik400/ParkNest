using Microsoft.Extensions.Logging;
using ParkNest.Application.Abstractions;

namespace ParkNest.Application.Notifications;

/// <summary>
/// Turns booking events into messages the two parties actually see.
///
/// A separate module from the booking service on purpose. Booking decides what happened to the
/// money and the session; deciding who should be told, and in what words, is a different concern
/// that changes for different reasons — and one that must never be able to fail a checkout.
///
/// Money figures are taken from the event rather than re-read. The message describes what happened
/// at that moment, and a balance fetched a second later is a different fact.
/// </summary>
public sealed class BookingNotificationHandlers :
    IEventHandler<BookingCreated>,
    IEventHandler<SessionStarted>,
    IEventHandler<SessionEnded>,
    IEventHandler<BookingCancelled>,
    IEventHandler<OverstayCharged>,
    IEventHandler<DisputeResolved>
{
    private readonly INotificationService _notifications;
    private readonly ILogger<BookingNotificationHandlers> _logger;

    public BookingNotificationHandlers(
        INotificationService notifications,
        ILogger<BookingNotificationHandlers> logger)
    {
        _notifications = notifications;
        _logger = logger;
    }

    public async Task HandleAsync(BookingCreated @event, CancellationToken cancellationToken = default)
    {
        await _notifications.CreateAsync(new CreateNotification(
            @event.RenterId,
            "booking.created",
            "Space reserved",
            $"{Credits(@event.HoldAmount)} is held until {Local(@event.ExpectedEndTime)}.",
            @event.BookingId), cancellationToken);

        // The host is told too. A booking against their space is something they may need to act on
        // — moving their own car out of the way, most obviously.
        await _notifications.CreateAsync(new CreateNotification(
            @event.HostId,
            "booking.created",
            "Your space is booked",
            $"Booked from {Local(@event.StartTime)} to {Local(@event.ExpectedEndTime)}.",
            @event.BookingId), cancellationToken);
    }

    public async Task HandleAsync(SessionStarted @event, CancellationToken cancellationToken = default)
    {
        await _notifications.CreateAsync(new CreateNotification(
            @event.RenterId,
            "session.started",
            "Session started",
            $"Your slot runs until {Local(@event.ExpectedEndTime)}. Staying longer bills automatically.",
            @event.BookingId), cancellationToken);
    }

    public async Task HandleAsync(SessionEnded @event, CancellationToken cancellationToken = default)
    {
        // A shortfall is the one outcome the renter must not be able to miss, so it is said first
        // and said plainly rather than buried in a receipt.
        var renterBody = @event.Shortfall > 0
            ? $"{Credits(@event.Shortfall)} could not be collected. Your access is restricted until it is settled."
            : $"{Credits(@event.TotalCharged)} charged for {@event.BilledMinutes} minutes. "
              + $"{Credits(@event.ReleasedToRenter)} released back to you.";

        await _notifications.CreateAsync(new CreateNotification(
            @event.RenterId,
            @event.Shortfall > 0 ? "session.violation" : "session.ended",
            @event.Shortfall > 0 ? "Payment incomplete" : "Session finished",
            renterBody,
            @event.BookingId), cancellationToken);

        await _notifications.CreateAsync(new CreateNotification(
            @event.HostId,
            "session.ended",
            "You earned credits",
            $"{Credits(@event.HostCredited)} added to your earnings.",
            @event.BookingId), cancellationToken);
    }

    public async Task HandleAsync(BookingCancelled @event, CancellationToken cancellationToken = default)
    {
        await _notifications.CreateAsync(new CreateNotification(
            @event.RenterId,
            "booking.cancelled",
            "Booking cancelled",
            @event.Fee > 0
                ? $"{Credits(@event.Refund)} returned. {Credits(@event.Fee)} went to the host for the late notice."
                : $"{Credits(@event.Refund)} returned in full.",
            @event.BookingId), cancellationToken);

        // The host needs to know their slot is free again, and whether they were compensated.
        await _notifications.CreateAsync(new CreateNotification(
            @event.HostId,
            "booking.cancelled",
            "A booking was cancelled",
            @event.Fee > 0
                ? $"Your slot is free again. {Credits(@event.Fee)} was paid for the late notice."
                : "Your slot is free again.",
            @event.BookingId), cancellationToken);
    }

    public async Task HandleAsync(OverstayCharged @event, CancellationToken cancellationToken = default)
    {
        // The whole point of metering while the session runs: the renter hears about it while they
        // can still do something, rather than at checkout when the sum has grown.
        var body = @event.Shortfall > 0
            ? $"{Credits(@event.Charged)} taken for {@event.OverstayMinutes} extra minutes, "
              + $"and {Credits(@event.Shortfall)} could not be covered. Add credits to avoid a violation."
            : $"{Credits(@event.Charged)} taken for {@event.OverstayMinutes} extra minutes.";

        await _notifications.CreateAsync(new CreateNotification(
            @event.RenterId,
            @event.Shortfall > 0 ? "overstay.short" : "overstay.charged",
            "You are past your slot",
            body,
            @event.BookingId), cancellationToken);

        _logger.LogInformation("Told renter {RenterId} about an overstay on {BookingId}.",
            @event.RenterId, @event.BookingId);
    }

    public async Task HandleAsync(DisputeResolved @event, CancellationToken cancellationToken = default)
    {
        // Both sides hear the same decision. A dispute settled where only the winner is told is
        // how the same complaint gets raised again a week later.
        var outcome = @event.RefundToRenter > 0
            ? $"{@event.Resolution} {Credits(@event.RefundToRenter)} returned to the renter."
            : @event.Resolution;

        foreach (var userId in new[] { @event.RenterId, @event.HostId })
        {
            await _notifications.CreateAsync(new CreateNotification(
                userId,
                "dispute.resolved",
                @event.Status == "Resolved" ? "Dispute upheld" : "Dispute closed",
                outcome,
                @event.BookingId), cancellationToken);
        }
    }

    private static string Credits(decimal amount) => $"₹{amount:0.00}";

    /// <summary>
    /// Rendered in the space's own terms rather than UTC. A message saying a slot ends at 04:30
    /// when the host set it to 10:00 is worse than no message.
    /// </summary>
    private static string Local(DateTimeOffset value) => value.ToLocalTime().ToString("d MMM, HH:mm");
}
