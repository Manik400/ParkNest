using ParkNest.Application.Abstractions;
using ParkNest.Domain.Analytics;

namespace ParkNest.Application.Analytics;

/// <summary>
/// Counts the things the product does, off the same events that drive notifications and metrics.
///
/// Subscribed rather than called from the booking service for the reason the Prometheus handlers
/// already are: a money path should not have a counter in the middle of it, and an increment
/// someone forgets to add to a new code path is a number that quietly lies. These arrive after
/// the transaction has committed, so a hit recorded here describes something that definitely
/// happened.
///
/// Payments are instrumented separately, in the payment service itself, because a payment
/// <i>attempt</i> is not an event the system publishes — nothing downstream cares that somebody
/// opened a checkout page, and the whole point of the funnel is the gap between the attempts and
/// the ones that landed.
/// </summary>
public sealed class AnalyticsEventHandlers :
    IEventHandler<BookingCreated>,
    IEventHandler<BookingCancelled>,
    IEventHandler<SessionEnded>,
    IEventHandler<DisputeRaised>
{
    private readonly IAnalyticsRecorder _recorder;

    public AnalyticsEventHandlers(IAnalyticsRecorder recorder) => _recorder = recorder;

    public Task HandleAsync(BookingCreated @event, CancellationToken cancellationToken = default) =>
        _recorder.RecordAsync(
            new AnalyticsHit(
                AnalyticsEventNames.BookingCreated,
                UserId: @event.RenterId,
                Amount: @event.HoldAmount),
            cancellationToken);

    public Task HandleAsync(BookingCancelled @event, CancellationToken cancellationToken = default) =>
        _recorder.RecordAsync(
            new AnalyticsHit(
                AnalyticsEventNames.BookingCancelled,
                UserId: @event.RenterId,
                Amount: @event.Refund,
                Detail: @event.Fee > 0 ? "late" : "in-window"),
            cancellationToken);

    public Task HandleAsync(SessionEnded @event, CancellationToken cancellationToken = default) =>
        _recorder.RecordAsync(
            new AnalyticsHit(
                AnalyticsEventNames.BookingCompleted,
                UserId: @event.RenterId,
                Amount: @event.TotalCharged,
                Detail: @event.Shortfall > 0 ? "shortfall" : "settled"),
            cancellationToken);

    public Task HandleAsync(DisputeRaised @event, CancellationToken cancellationToken = default) =>
        _recorder.RecordAsync(
            new AnalyticsHit(
                AnalyticsEventNames.DisputeRaised,
                UserId: @event.RaisedByUserId),
            cancellationToken);
}
