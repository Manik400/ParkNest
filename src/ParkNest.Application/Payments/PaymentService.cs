using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;
using ParkNest.Domain.Payments;

namespace ParkNest.Application.Payments;

public interface IPaymentService
{
    /// <summary>
    /// Starts a credit purchase for the authenticated caller. <paramref name="returnTo"/> names the
    /// client that started it ("admin", "app"), which decides where the browser lands afterwards.
    /// </summary>
    Task<StartPaymentResult> StartAsync(
        decimal amount,
        string? returnTo = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles a gateway callback. Verifies it first, then credits the wallet using the amount
    /// recorded when the order was created — never an amount taken from the callback.
    /// </summary>
    Task<WebhookResult> HandleWebhookAsync(WebhookRequest request, CancellationToken cancellationToken = default);

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
    private readonly IPaymentSettlement _settlement;
    private readonly IPaymentReconciler _reconciler;
    private readonly PaymentUrls _urls;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly PlatformOptions _platform;
    private readonly PaymentOptions _payments;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(
        IParkNestDbContext db,
        IPaymentGateway gateway,
        IPaymentSettlement settlement,
        IPaymentReconciler reconciler,
        PaymentUrls urls,
        ICurrentUser currentUser,
        IClock clock,
        IOptions<PlatformOptions> platform,
        IOptions<PaymentOptions> payments,
        ILogger<PaymentService> logger)
    {
        _db = db;
        _gateway = gateway;
        _settlement = settlement;
        _reconciler = reconciler;
        _urls = urls;
        _currentUser = currentUser;
        _clock = clock;
        _platform = platform.Value;
        _payments = payments.Value;
        _logger = logger;
    }

    public async Task<StartPaymentResult> StartAsync(
        decimal amount,
        string? returnTo = null,
        CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();
        amount = Money.Round(amount);

        if (amount < _platform.MinimumRechargeCredits)
        {
            throw new DomainException($"Minimum recharge is {_platform.MinimumRechargeCredits:0.00} credits.");
        }

        if (amount > _platform.MaximumRechargeCredits)
        {
            // An upper bound keeps a fat-fingered or scripted order from parking an absurd sum in
            // the escrow account, and caps the blast radius of a compromised session.
            throw new DomainException($"Maximum recharge is {_platform.MaximumRechargeCredits:0.00} credits.");
        }

        var target = PaymentUrls.Normalise(returnTo);

        if (!_urls.IsKnownReturnTo(target))
        {
            throw new DomainException($"Unknown return target '{target}'.");
        }

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        var now = _clock.UtcNow;

        var order = new PaymentOrder
        {
            UserId = userId,
            Amount = amount,
            Currency = "INR",
            Status = PaymentOrderStatus.Created,
            ReturnTo = target,
            CreatedAt = now
        };

        var gatewayOrder = await _gateway.CreateOrderAsync(
            new GatewayOrderRequest(
                order.Id,
                amount,
                order.Currency,
                new PaymentCustomer(
                    userId,
                    user?.Phone,
                    user?.Email,
                    string.IsNullOrWhiteSpace(user?.FullName) ? null : user.FullName),
                _urls.ReturnUrl(order.Id),
                _urls.WebhookUrl,
                // Matched to our own sweep, so the provider stops taking money for an order at
                // about the moment we stop expecting it.
                now.AddMinutes(_payments.OrderExpiryMinutes)),
            cancellationToken);

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

        if (IsDueForReconcile(order))
        {
            // The client is polling because it has not heard. Ask the gateway rather than wait on
            // a webhook that may never come — but not on every poll: the app asks every couple of
            // seconds, and each ask here is a call to the provider.
            await _reconciler.ReconcileAsync(order.Id, "poll", cancellationToken);
        }

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
        WebhookRequest request,
        CancellationToken cancellationToken = default)
    {
        // Verification first, before the body is trusted enough even to parse. An unverified
        // caller hitting this endpoint must not be able to mint credits — that is the entire point.
        if (!_gateway.VerifyWebhook(request))
        {
            _logger.LogWarning("Rejected a {Gateway} webhook that failed verification.", _gateway.Name);
            throw new UnauthorizedException("Invalid webhook signature.");
        }

        var outcome = _gateway.ParseWebhook(request.RawBody);

        if (!_gateway.WebhookIsAuthoritative)
        {
            // Verification proved who sent it, not what it says. Ask the gateway itself and apply
            // that answer instead; if it cannot be asked, apply nothing.
            var confirmed = await _gateway.QueryOrderAsync(outcome.ProviderOrderId, cancellationToken);
            outcome = confirmed ?? outcome with { Kind = GatewayOutcomeKind.Pending };
        }

        var order = await _db.PaymentOrders
            .FirstOrDefaultAsync(o => o.ProviderOrderId == outcome.ProviderOrderId, cancellationToken);

        if (order is null)
        {
            // Verified but unknown: acknowledge so the gateway stops retrying, and log it, because
            // it means our records and theirs disagree.
            _logger.LogWarning("Verified webhook for unknown order {ProviderOrderId}.", outcome.ProviderOrderId);
            return new WebhookResult(false, "Unknown order.");
        }

        var result = await _settlement.ApplyAsync(order, outcome, "webhook", cancellationToken);

        return new WebhookResult(true, result.Message);
    }

    private bool IsDueForReconcile(PaymentOrder order)
    {
        if (order.Status != PaymentOrderStatus.Created)
        {
            return false;
        }

        var now = _clock.UtcNow;

        return now - order.CreatedAt >= TimeSpan.FromSeconds(_payments.ReconcileAfterSeconds)
               && (order.LastCheckedAt is null
                   || now - order.LastCheckedAt.Value >= TimeSpan.FromSeconds(_payments.ReconcileMinIntervalSeconds));
    }
}
