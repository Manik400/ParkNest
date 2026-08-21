namespace ParkNest.Application.Payments;

/// <summary>
/// The payment aggregator. Only two operations matter to ParkNest: start a payment, and prove that
/// a callback claiming one succeeded really came from the gateway.
///
/// Keeping the surface this narrow is deliberate — per ADR 0004, real money crosses the boundary
/// in exactly two places, so the compliance posture is a property of a couple of code paths rather
/// than of the whole system.
/// </summary>
public interface IPaymentGateway
{
    string Name { get; }

    /// <summary>
    /// Header the gateway puts its signature in. Each provider picks its own, so the webhook
    /// endpoint asks rather than hard-coding one — otherwise swapping providers silently breaks
    /// verification, and a webhook that cannot be verified is a webhook that cannot be trusted.
    /// </summary>
    string SignatureHeader { get; }

    /// <summary>Registers an order with the gateway and returns what the client needs to pay it.</summary>
    Task<GatewayOrder> CreateOrderAsync(
        Guid orderId,
        decimal amount,
        string currency,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True only if <paramref name="signature"/> is a valid signature over the exact raw body.
    /// Must be constant-time — a timing-variable compare leaks the expected signature byte by byte.
    /// </summary>
    bool VerifyWebhookSignature(string rawBody, string signature);

    /// <summary>Pulls the order reference, payment reference and outcome out of a webhook body.</summary>
    WebhookEvent ParseWebhook(string rawBody);
}

/// <param name="CheckoutPayload">
/// Opaque data the client SDK needs to open the payment sheet (keys, order id, and so on).
/// </param>
public sealed record GatewayOrder(string ProviderOrderId, string CheckoutPayload);

public sealed record WebhookEvent(
    string ProviderOrderId,
    string? ProviderPaymentId,
    bool Succeeded,
    string? FailureReason);
