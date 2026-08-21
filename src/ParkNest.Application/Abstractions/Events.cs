namespace ParkNest.Application.Abstractions;

/// <summary>
/// Something that happened, stated as fact and in the past tense.
///
/// Events are not commands. Publishing one must never be how a thing gets done — the booking is
/// already made and the credits already moved by the time anyone hears about it. That distinction
/// is what makes a broker outage a degraded notification rather than a lost booking.
/// </summary>
public interface IIntegrationEvent
{
    /// <summary>
    /// Routing key and log line. A stable string rather than a type name, because the wire format
    /// must not change when someone renames a class.
    /// </summary>
    static abstract string EventName { get; }
}

/// <summary>Publishes events to whoever is listening.</summary>
public interface IEventBus
{
    /// <summary>
    /// Announces something that already happened.
    ///
    /// Never throws for delivery reasons. A caller that has just settled a booking cannot undo it
    /// because a broker was unreachable, and letting a publish failure escape would turn a
    /// notification outage into a failed checkout.
    /// </summary>
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent;
}

/// <summary>Handles one kind of event. Registered per event type; several may handle the same one.</summary>
public interface IEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken = default);
}

// --- The events themselves ------------------------------------------------
//
// Each carries the ids a handler needs plus the figures it would otherwise have to re-read. That
// is deliberate duplication: a handler running a second later, or on another machine, should not
// have to hit the database to write a notification, and should describe what happened *then*
// rather than what is true now.

public sealed record BookingCreated(
    Guid BookingId,
    Guid RenterId,
    Guid HostId,
    Guid ParkingSpaceId,
    DateTimeOffset StartTime,
    DateTimeOffset ExpectedEndTime,
    decimal HoldAmount) : IIntegrationEvent
{
    public static string EventName => "booking.created";
}

public sealed record SessionStarted(
    Guid BookingId,
    Guid RenterId,
    Guid HostId,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpectedEndTime,
    string DetectionMethod) : IIntegrationEvent
{
    public static string EventName => "booking.session-started";
}

public sealed record SessionEnded(
    Guid BookingId,
    Guid RenterId,
    Guid HostId,
    int BilledMinutes,
    decimal TotalCharged,
    decimal ReleasedToRenter,
    decimal HostCredited,
    decimal Shortfall) : IIntegrationEvent
{
    public static string EventName => "booking.session-ended";
}

public sealed record BookingCancelled(
    Guid BookingId,
    Guid RenterId,
    Guid HostId,
    decimal Fee,
    decimal Refund) : IIntegrationEvent
{
    public static string EventName => "booking.cancelled";
}

/// <param name="Shortfall">Non-zero means the renter could not cover what the meter asked for.</param>
public sealed record OverstayCharged(
    Guid BookingId,
    Guid RenterId,
    int OverstayMinutes,
    decimal Charged,
    decimal Shortfall) : IIntegrationEvent
{
    public static string EventName => "booking.overstay-charged";
}

public sealed record DisputeRaised(
    Guid DisputeId,
    Guid BookingId,
    Guid RaisedByUserId,
    Guid RenterId,
    Guid HostId,
    string Reason) : IIntegrationEvent
{
    public static string EventName => "dispute.raised";
}

public sealed record DisputeResolved(
    Guid DisputeId,
    Guid BookingId,
    Guid RenterId,
    Guid HostId,
    string Status,
    string Resolution,
    decimal RefundToRenter) : IIntegrationEvent
{
    public static string EventName => "dispute.resolved";
}

public sealed record WalletChanged(
    Guid UserId,
    Guid WalletId,
    decimal Spendable,
    decimal Held,
    decimal Earning,
    string Reason) : IIntegrationEvent
{
    public static string EventName => "wallet.changed";
}

/// <param name="Reason">Why it was refused, for the message the host gets. Null when approved.</param>
public sealed record KycReviewed(
    Guid UserId,
    bool Verified,
    string? Reason) : IIntegrationEvent
{
    public static string EventName => "kyc.reviewed";
}
