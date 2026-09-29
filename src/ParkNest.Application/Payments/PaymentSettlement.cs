using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Analytics;
using ParkNest.Application.Wallets;
using ParkNest.Domain.Analytics;
using ParkNest.Domain.Common;
using ParkNest.Domain.Payments;

namespace ParkNest.Application.Payments;

/// <summary>
/// The one place a gateway's verdict on an order turns into credits.
///
/// A payment can be learned about three ways — a verified webhook, the browser's return trip, or
/// the background sweep asking the gateway — and they race. All three come through here, and the
/// ledger's idempotency key is derived from the order id, so however many of them arrive and in
/// whatever order, an order credits exactly once.
/// </summary>
public interface IPaymentSettlement
{
    /// <param name="source">Where the verdict came from — "webhook", "return", "poll", "sweep". Logged only.</param>
    Task<SettlementResult> ApplyAsync(
        PaymentOrder order,
        GatewayOutcome outcome,
        string source,
        CancellationToken cancellationToken = default);
}

public sealed record SettlementResult(bool Changed, string Message);

public sealed class PaymentSettlement : IPaymentSettlement
{
    private readonly IParkNestDbContext _db;
    private readonly IWalletService _wallets;
    private readonly IAnalyticsRecorder _analytics;
    private readonly IClock _clock;
    private readonly ILogger<PaymentSettlement> _logger;

    public PaymentSettlement(
        IParkNestDbContext db,
        IWalletService wallets,
        IAnalyticsRecorder analytics,
        IClock clock,
        ILogger<PaymentSettlement> logger)
    {
        _db = db;
        _wallets = wallets;
        _analytics = analytics;
        _clock = clock;
        _logger = logger;
    }

    public async Task<SettlementResult> ApplyAsync(
        PaymentOrder order,
        GatewayOutcome outcome,
        string source,
        CancellationToken cancellationToken = default)
    {
        if (order.Status == PaymentOrderStatus.Paid)
        {
            // Gateways retry until acknowledged, and three paths race to report the same payment,
            // so duplicates are expected, not exceptional.
            return new SettlementResult(false, "Already processed.");
        }

        if (outcome.Kind == GatewayOutcomeKind.Pending)
        {
            return new SettlementResult(false, "Noted; nothing to apply yet.");
        }

        if (outcome.ReportedAmount is { } reported && Money.Round(reported) != order.Amount)
        {
            // The credit would come from our own record regardless, so this is not a way to steal —
            // it is a sign that our records and the gateway's disagree about what was paid, which
            // a human has to look at before anything is issued.
            _logger.LogCritical(
                "Payment order {OrderId}: {Source} reported {Reported} paid, but the order is for {Amount}. Not applied.",
                order.Id, source, reported, order.Amount);

            return new SettlementResult(false, "Amount mismatch; left for review.");
        }

        if (order.Status == PaymentOrderStatus.Cancelled)
        {
            // We gave up on this order; the payer did not. Expiry is our own bookkeeping and says
            // nothing about whether money moved, so a verified outcome still applies — refusing
            // would take the money and hand back nothing. Logged, because it means the expiry
            // window is shorter than what the gateway actually takes to confirm.
            _logger.LogWarning(
                "Payment order {OrderId} was expired locally but the gateway has now reported on it ({Source}).",
                order.Id, source);
        }

        order.ProviderPaymentId = outcome.ProviderPaymentId ?? order.ProviderPaymentId;
        order.CompletedAt = _clock.UtcNow;

        if (outcome.Kind == GatewayOutcomeKind.Failed)
        {
            order.Status = PaymentOrderStatus.Failed;
            order.FailureReason = outcome.FailureReason;
            await _db.SaveChangesAsync(cancellationToken);

            await _analytics.RecordAsync(
                new AnalyticsHit(
                    AnalyticsEventNames.PaymentFailed,
                    UserId: order.UserId,
                    Amount: order.Amount,
                    Detail: source),
                cancellationToken);

            return new SettlementResult(true, "Recorded failure.");
        }

        // The amount comes from our own record, not the gateway's. A tampered body claiming a
        // larger sum cannot inflate the credit — and the idempotency key is derived from the
        // order id, so a replay, or a webhook racing a status check, cannot credit twice either.
        await _wallets.RechargeAsync(
            order.UserId,
            order.Amount,
            $"payment:{order.Id}",
            cancellationToken);

        var transaction = await _db.LedgerTransactions
            .FirstOrDefaultAsync(t => t.IdempotencyKey == $"payment:{order.Id}", cancellationToken);

        order.LedgerTransactionId = transaction?.Id;
        order.Status = PaymentOrderStatus.Paid;
        // Clears an earlier failure or the expiry note, so a paid order never carries a reason it failed.
        order.FailureReason = null;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Credited {Amount} to {UserId} for payment order {OrderId} ({Source}).",
            order.Amount, order.UserId, order.Id, source);

        // Counted here rather than at the webhook, because this is the one place all three routes
        // to a verdict converge and the one place an order can only pass through once — so the
        // funnel cannot double-count a gateway that retries.
        await _analytics.RecordAsync(
            new AnalyticsHit(
                AnalyticsEventNames.PaymentSucceeded,
                UserId: order.UserId,
                Amount: order.Amount,
                Detail: source),
            cancellationToken);

        return new SettlementResult(true, "Credited.");
    }
}
