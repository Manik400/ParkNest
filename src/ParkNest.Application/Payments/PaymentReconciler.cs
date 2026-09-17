using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Domain.Payments;

namespace ParkNest.Application.Payments;

/// <summary>
/// Asks the gateway where a payment stands, rather than waiting to be told.
///
/// Webhooks are the fast path, but they get lost: a deploy in progress, a provider outage, or —
/// every single time in local development — an API with no public address for the provider to
/// call. Without this, a user whose money has moved would sit with no credits until someone
/// noticed. With it, the browser's return trip, the client's polling and the background sweep all
/// ask, and the answer goes through the same <see cref="IPaymentSettlement"/> as a webhook would.
///
/// Deliberately free of <see cref="ICurrentUser"/>: the sweep calls it with nobody signed in, and
/// the return endpoint calls it for a browser that carries no token.
/// </summary>
public interface IPaymentReconciler
{
    /// <summary>Asks about one order and applies the answer. True when that changed the order.</summary>
    Task<bool> ReconcileAsync(Guid orderId, string source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks about orders still waiting, oldest first and at most <paramref name="max"/> per pass so
    /// the calls to the provider stay bounded. Returns how many changed.
    /// </summary>
    Task<int> ReconcilePendingAsync(int max = 50, CancellationToken cancellationToken = default);
}

public sealed class PaymentReconciler : IPaymentReconciler
{
    /// <summary>
    /// How old an order must be before the sweep asks about it. Younger ones are almost always
    /// someone still at the checkout sheet, and asking about them is a wasted call.
    /// </summary>
    private static readonly TimeSpan SweepMinimumAge = TimeSpan.FromSeconds(60);

    private readonly IParkNestDbContext _db;
    private readonly IPaymentGateway _gateway;
    private readonly IPaymentSettlement _settlement;
    private readonly IClock _clock;
    private readonly PaymentOptions _options;
    private readonly ILogger<PaymentReconciler> _logger;

    public PaymentReconciler(
        IParkNestDbContext db,
        IPaymentGateway gateway,
        IPaymentSettlement settlement,
        IClock clock,
        IOptions<PaymentOptions> options,
        ILogger<PaymentReconciler> logger)
    {
        _db = db;
        _gateway = gateway;
        _settlement = settlement;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> ReconcileAsync(Guid orderId, string source, CancellationToken cancellationToken = default)
    {
        var order = await _db.PaymentOrders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

        return order is not null && await ReconcileAsync(order, source, cancellationToken);
    }

    public async Task<int> ReconcilePendingAsync(int max = 50, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var createdBefore = now - SweepMinimumAge;
        var checkedBefore = now.AddSeconds(-_options.ReconcileMinIntervalSeconds);

        var due = await _db.PaymentOrders
            .Where(o => o.Status == PaymentOrderStatus.Created
                        && o.CreatedAt <= createdBefore
                        && (o.LastCheckedAt == null || o.LastCheckedAt <= checkedBefore))
            .OrderBy(o => o.CreatedAt)
            .Take(max)
            .ToListAsync(cancellationToken);

        var changed = 0;

        foreach (var order in due)
        {
            if (await ReconcileAsync(order, "sweep", cancellationToken))
            {
                changed++;
            }
        }

        if (changed > 0)
        {
            _logger.LogInformation(
                "Reconciliation settled {Changed} of {Checked} waiting payment orders with {Gateway}.",
                changed, due.Count, _gateway.Name);
        }

        return changed;
    }

    private async Task<bool> ReconcileAsync(PaymentOrder order, string source, CancellationToken cancellationToken)
    {
        // Only an order still open can be moved by what the gateway says now. A failed one counts:
        // checkouts let the user retry after a failed attempt, and in development, with no
        // webhook, the return trip is the only thing that will ever notice the retry succeeded.
        if (order.Status is not (PaymentOrderStatus.Created or PaymentOrderStatus.Failed))
        {
            return false;
        }

        order.LastCheckedAt = _clock.UtcNow;

        GatewayOutcome? outcome;

        try
        {
            outcome = await _gateway.QueryOrderAsync(order.ProviderOrderId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A provider hiccup must not fail the user's poll or kill the sweep. The next ask, a
            // few seconds or minutes from now, will get the answer.
            _logger.LogWarning(
                ex,
                "Could not ask {Gateway} about payment order {OrderId} ({Source}); will ask again.",
                _gateway.Name, order.Id, source);

            await _db.SaveChangesAsync(cancellationToken);
            return false;
        }

        // A failed order moves only on news of success; being told again that it failed is not news.
        var nothingToApply = outcome is null
                             || outcome.Kind == GatewayOutcomeKind.Pending
                             || (order.Status == PaymentOrderStatus.Failed && outcome.Kind != GatewayOutcomeKind.Paid);

        if (nothingToApply)
        {
            await _db.SaveChangesAsync(cancellationToken);
            return false;
        }

        var result = await _settlement.ApplyAsync(order, outcome!, source, cancellationToken);

        return result.Changed;
    }
}
