using Microsoft.AspNetCore.SignalR;
using ParkNest.Application.Abstractions;

namespace ParkNest.Api.Realtime;

/// <summary>
/// Pushes the events that matter live down the socket.
///
/// A separate handler from the one that writes notifications, subscribed to the same events. That
/// split is the point of an event bus: the durable record and the instant nudge have different
/// failure modes — a socket push to a phone in a basement is simply lost, and must not be the only
/// place the message existed.
///
/// Lives in the API project rather than Application because a hub context is a delivery mechanism,
/// and the application layer should not know one exists.
/// </summary>
public sealed class RealtimeEventHandlers :
    IEventHandler<SessionStarted>,
    IEventHandler<SessionEnded>,
    IEventHandler<OverstayCharged>,
    IEventHandler<NextSlotBlocked>,
    IEventHandler<WalletChanged>,
    IEventHandler<BookingCancelled>
{
    private readonly IHubContext<ParkNestHub> _hub;

    public RealtimeEventHandlers(IHubContext<ParkNestHub> hub) => _hub = hub;

    public Task HandleAsync(SessionStarted @event, CancellationToken cancellationToken = default) =>
        // Both parties: the renter gets a running timer, the host sees their space go busy.
        SendAsync(new[] { @event.RenterId, @event.HostId }, "sessionStarted", new
        {
            bookingId = @event.BookingId,
            startedAt = @event.StartedAt,
            expectedEndTime = @event.ExpectedEndTime,
        }, cancellationToken);

    public Task HandleAsync(SessionEnded @event, CancellationToken cancellationToken = default) =>
        SendAsync(new[] { @event.RenterId, @event.HostId }, "sessionEnded", new
        {
            bookingId = @event.BookingId,
            billedMinutes = @event.BilledMinutes,
            totalCharged = @event.TotalCharged,
            releasedToRenter = @event.ReleasedToRenter,
            hostCredited = @event.HostCredited,
            shortfall = @event.Shortfall,
        }, cancellationToken);

    public Task HandleAsync(OverstayCharged @event, CancellationToken cancellationToken = default) =>
        // The one push worth interrupting someone for: money is leaving their balance right now,
        // and they can stop it by moving the car.
        SendAsync(new[] { @event.RenterId }, "overstayCharged", new
        {
            bookingId = @event.BookingId,
            overstayMinutes = @event.OverstayMinutes,
            charged = @event.Charged,
            shortfall = @event.Shortfall,
        }, cancellationToken);

    public Task HandleAsync(NextSlotBlocked @event, CancellationToken cancellationToken = default) =>
        // All three, and each acts on it differently: the blocked renter should stop driving, the
        // blocking renter should move, the host should go and look. One payload, because they are
        // all being told the same fact about the same space.
        SendAsync(new[] { @event.BlockedRenterId, @event.BlockingRenterId, @event.HostId }, "slotBlocked", new
        {
            blockedBookingId = @event.BlockedBookingId,
            blockingBookingId = @event.BlockingBookingId,
            parkingSpaceId = @event.ParkingSpaceId,
            blockedStartTime = @event.BlockedStartTime,
        }, cancellationToken);

    public Task HandleAsync(WalletChanged @event, CancellationToken cancellationToken = default) =>
        SendAsync(new[] { @event.UserId }, "walletChanged", new
        {
            spendable = @event.Spendable,
            held = @event.Held,
            earning = @event.Earning,
            reason = @event.Reason,
        }, cancellationToken);

    public Task HandleAsync(BookingCancelled @event, CancellationToken cancellationToken = default) =>
        SendAsync(new[] { @event.RenterId, @event.HostId }, "bookingCancelled", new
        {
            bookingId = @event.BookingId,
            fee = @event.Fee,
            refund = @event.Refund,
        }, cancellationToken);

    private Task SendAsync(IEnumerable<Guid> userIds, string method, object payload, CancellationToken cancellationToken)
    {
        var groups = userIds.Distinct().Select(ParkNestHub.UserGroup).ToList();

        // Nothing is awaited into the caller's failure path: a disconnected client is the normal
        // case, not an error, and the durable notification has already been written by the time
        // this runs.
        return _hub.Clients.Groups(groups).SendAsync(method, payload, cancellationToken);
    }
}
