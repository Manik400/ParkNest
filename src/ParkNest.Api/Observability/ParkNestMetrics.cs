using Prometheus;
using ParkNest.Application.Abstractions;

namespace ParkNest.Api.Observability;

/// <summary>
/// The business metrics worth an alert, as opposed to the HTTP and runtime ones prometheus-net
/// collects on its own.
///
/// Chosen from PRD §17: booking volume, ledger reconciliation drift, dispute rate. The first is
/// how you know the marketplace is alive, the second is how you know the money is right, and the
/// third is how you know the detection tier is good enough.
///
/// No user ids or booking ids appear in a label. Every distinct label value is a separate time
/// series held in memory forever — an id there is not a metric, it is a memory leak with a
/// dashboard.
/// </summary>
public static class ParkNestMetrics
{
    public static readonly Counter BookingsCreated = Metrics.CreateCounter(
        "parknest_bookings_created_total",
        "Bookings reserved, whether or not the session went ahead.");

    public static readonly Counter SessionsEnded = Metrics.CreateCounter(
        "parknest_sessions_ended_total",
        "Sessions settled, split by whether the renter covered what they owed.",
        new CounterConfiguration { LabelNames = new[] { "outcome" } });

    public static readonly Counter BookingsCancelled = Metrics.CreateCounter(
        "parknest_bookings_cancelled_total",
        "Cancellations, split by whether the host was compensated.",
        new CounterConfiguration { LabelNames = new[] { "timing" } });

    public static readonly Counter OverstaysCharged = Metrics.CreateCounter(
        "parknest_overstays_charged_total",
        "Overstay debits taken by the meter while sessions were still running.");

    public static readonly Counter CreditsSettled = Metrics.CreateCounter(
        "parknest_credits_settled_total",
        "Credits moved from renters to hosts and the platform at settlement.");

    /// <summary>
    /// Credits owed at checkout that could not be collected. The number that says whether the
    /// prepaid model is holding: it should be near zero, and a rise means detection or the meter
    /// is letting people run up debts.
    /// </summary>
    public static readonly Counter Shortfall = Metrics.CreateCounter(
        "parknest_shortfall_credits_total",
        "Credits a renter owed at settlement and could not cover.");

    /// <summary>
    /// The one to alert on. Balances are a cached projection of the ledger, and any gap between
    /// them is an incident rather than a slow day — this is a gauge because the question is always
    /// "is it wrong right now", never "how often has it been wrong".
    /// </summary>
    public static readonly Gauge ReconciliationDrift = Metrics.CreateGauge(
        "parknest_reconciliation_drift_credits",
        "Absolute difference between stored wallet balances and the ledger replayed from entries.");

    public static readonly Gauge WalletsChecked = Metrics.CreateGauge(
        "parknest_reconciliation_wallets_checked",
        "Wallets included in the last reconciliation sweep.");

    public static readonly Counter DisputesRaised = Metrics.CreateCounter(
        "parknest_disputes_raised_total",
        "Disputes opened against a booking.");
}

/// <summary>
/// Counts what happened, off the same events that drive notifications.
///
/// Subscribed rather than instrumented in-line: a booking service that reached for a counter would
/// be doing observability in the middle of a money path, and the numbers would drift the moment
/// someone added a code path and forgot the increment.
/// </summary>
public sealed class MetricsEventHandlers :
    IEventHandler<BookingCreated>,
    IEventHandler<SessionEnded>,
    IEventHandler<BookingCancelled>,
    IEventHandler<OverstayCharged>,
    IEventHandler<DisputeRaised>
{
    public Task HandleAsync(BookingCreated @event, CancellationToken cancellationToken = default)
    {
        ParkNestMetrics.BookingsCreated.Inc();
        return Task.CompletedTask;
    }

    public Task HandleAsync(SessionEnded @event, CancellationToken cancellationToken = default)
    {
        ParkNestMetrics.SessionsEnded
            .WithLabels(@event.Shortfall > 0 ? "violation" : "completed")
            .Inc();

        ParkNestMetrics.CreditsSettled.Inc((double)@event.TotalCharged);

        if (@event.Shortfall > 0)
        {
            ParkNestMetrics.Shortfall.Inc((double)@event.Shortfall);
        }

        return Task.CompletedTask;
    }

    public Task HandleAsync(BookingCancelled @event, CancellationToken cancellationToken = default)
    {
        ParkNestMetrics.BookingsCancelled
            .WithLabels(@event.Fee > 0 ? "late" : "in-window")
            .Inc();

        return Task.CompletedTask;
    }

    public Task HandleAsync(OverstayCharged @event, CancellationToken cancellationToken = default)
    {
        ParkNestMetrics.OverstaysCharged.Inc();
        return Task.CompletedTask;
    }

    public Task HandleAsync(DisputeRaised @event, CancellationToken cancellationToken = default)
    {
        ParkNestMetrics.DisputesRaised.Inc();
        return Task.CompletedTask;
    }
}
