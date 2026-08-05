using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Application.Wallets;
using ParkNest.Domain.Common;
using ParkNest.Domain.Payments;

namespace ParkNest.Application.Payments;

public interface IPaymentService
{
    /// <summary>Starts a credit purchase for the authenticated caller.</summary>
    Task<StartPaymentResult> StartAsync(decimal amount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles a gateway callback. Verifies the signature first, then credits the wallet using the
    /// amount recorded when the order was created — never an amount taken from the callback.
    /// </summary>
    Task<WebhookResult> HandleWebhookAsync(string rawBody, string signature, CancellationToken cancellationToken = default);

    /// <summary>
    /// Where an order stands, for the caller who owns it. This is how a client learns a payment
    /// succeeded — the browser's return from checkout proves only that the user came back.
    /// </summary>
    Task<PaymentOrderView> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default);
}

public sealed record StartPaymentResult(Guid OrderId, string ProviderOrderId, decimal Amount, string CheckoutPayload);

public sealed record WebhookResult(bool Accepted, string Message);

public sealed record PaymentOrderView(
    Guid OrderId,
    string ProviderOrderId,
    decimal Amount,
    string Currency,
    string Status,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed class PaymentService : IPaymentService
{
    private readonly IParkNestDbContext _db;
    private readonly IPaymentGateway _gateway;
    private readonly IWalletService _wallets;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly PlatformOptions _options;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(
        IParkNestDbContext db,
        IPaymentGateway gateway,
        IWalletService wallets,
        ICurrentUser currentUser,
        IClock clock,
        IOptions<PlatformOptions> options,
        ILogger<PaymentService> logger)
    {
        _db = db;
        _gateway = gateway;
        _wallets = wallets;
        _currentUser = currentUser;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<StartPaymentResult> StartAsync(decimal amount, CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();
        amount = Money.Round(amount);

        if (amount < _options.MinimumRechargeCredits)
        {
            throw new DomainException($"Minimum recharge is {_options.MinimumRechargeCredits:0.00} credits.");
        }

        if (amount > _options.MaximumRechargeCredits)
        {
            // An upper bound keeps a fat-fingered or scripted order from parking an absurd sum in
            // the escrow account, and caps the blast radius of a compromised session.
            throw new DomainException($"Maximum recharge is {_options.MaximumRechargeCredits:0.00} credits.");
        }

        var order = new PaymentOrder
        {
            UserId = userId,
            Amount = amount,
            Currency = "INR",
            Status = PaymentOrderStatus.Created,
            CreatedAt = _clock.UtcNow
        };

        var gatewayOrder = await _gateway.CreateOrderAsync(order.Id, amount, order.Currency, cancellationToken);

        order.ProviderOrderId = gatewayOrder.ProviderOrderId;

        _db.PaymentOrders.Add(order);
        await _db.SaveChangesAsync(cancellationToken);

        return new StartPaymentResult(order.Id, order.ProviderOrderId, order.Amount, gatewayOrder.CheckoutPayload);
    }

    public async Task<PaymentOrderView> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await _db.PaymentOrders
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken)
            ?? throw new DomainException($"Payment order {orderId} does not exist.");

        // Your own orders, or an admin's. Order ids are guessable enough in aggregate that leaving
        // this open would let one user enumerate another's recharge history and amounts.
        _currentUser.RequireSelfOrAdmin(order.UserId);

        return new PaymentOrderView(
            order.Id,
            order.ProviderOrderId,
            order.Amount,
            order.Currency,
            order.Status.ToString(),
            order.FailureReason,
            order.CreatedAt,
            order.CompletedAt);
    }

    public async Task<WebhookResult> HandleWebhookAsync(
        string rawBody,
        string signature,
        CancellationToken cancellationToken = default)
    {
        // Signature first, before the body is trusted enough even to parse. An unsigned caller
        // hitting this endpoint must not be able to mint credits — that is the entire point.
        if (!_gateway.VerifyWebhookSignature(rawBody, signature))
        {
            _logger.LogWarning("Rejected a {Gateway} webhook with an invalid signature.", _gateway.Name);
            throw new UnauthorizedException("Invalid webhook signature.");
        }

        var evt = _gateway.ParseWebhook(rawBody);

        var order = await _db.PaymentOrders
            .FirstOrDefaultAsync(o => o.ProviderOrderId == evt.ProviderOrderId, cancellationToken);

        if (order is null)
        {
            // Signed but unknown: acknowledge so the gateway stops retrying, and log it, because
            // it means our records and theirs disagree.
            _logger.LogWarning("Verified webhook for unknown order {ProviderOrderId}.", evt.ProviderOrderId);
            return new WebhookResult(false, "Unknown order.");
        }

        if (order.Status == PaymentOrderStatus.Paid)
        {
            // Gateways retry until acknowledged, so duplicates are expected, not exceptional.
            return new WebhookResult(true, "Already processed.");
        }

        order.ProviderPaymentId = evt.ProviderPaymentId;
        order.CompletedAt = _clock.UtcNow;

        if (!evt.Succeeded)
        {
            order.Status = PaymentOrderStatus.Failed;
            order.FailureReason = evt.FailureReason;
            await _db.SaveChangesAsync(cancellationToken);
            return new WebhookResult(true, "Recorded failure.");
        }

        // The amount comes from our own record, not the payload. A tampered body claiming a
        // larger sum cannot inflate the credit — and the idempotency key is derived from the
        // order id, so a replayed webhook cannot credit twice either.
        await _wallets.RechargeAsync(
            order.UserId,
            order.Amount,
            $"payment:{order.Id}",
            cancellationToken);

        var transaction = await _db.LedgerTransactions
            .FirstOrDefaultAsync(t => t.IdempotencyKey == $"payment:{order.Id}", cancellationToken);

        order.LedgerTransactionId = transaction?.Id;
        order.Status = PaymentOrderStatus.Paid;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Credited {Amount} to {UserId} for payment order {OrderId}.",
            order.Amount, order.UserId, order.Id);

        return new WebhookResult(true, "Credited.");
    }
}
