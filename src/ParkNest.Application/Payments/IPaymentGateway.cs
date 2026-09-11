namespace ParkNest.Application.Payments;

/// <summary>
/// The payment aggregator. Three things matter to ParkNest: start a payment, prove that a callback
/// claiming one succeeded really came from the gateway, and — because callbacks get lost — ask the
/// gateway directly where a payment stands.
///
/// Keeping the surface this narrow is deliberate — per ADR 0004, real money crosses the boundary
/// in exactly two places, so the compliance posture is a property of a couple of code paths rather
/// than of the whole system. Which provider sits behind it is configuration (ADR 0007).
/// </summary>
public interface IPaymentGateway
{
    string Name { get; }

    /// <summary>
    /// Whether a verified webhook can be applied as it stands. False when verification proves only
    /// who sent it, not what it says — a fixed credential in a header rather than a signature over
    /// the body — in which case the service confirms through <see cref="QueryOrderAsync"/> first.
    /// </summary>
    bool WebhookIsAuthoritative { get; }

    /// <summary>Registers an order with the gateway and returns what the client needs to pay it.</summary>
    Task<GatewayOrder> CreateOrderAsync(GatewayOrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// True only if the request provably came from the gateway. Takes every header because
    /// providers disagree about where the proof lives — one signature header, a signature plus a
    /// timestamp, or an Authorization value. Must compare in constant time: a short-circuiting
    /// compare leaks the expected value byte by byte to anyone willing to time the response.
    /// </summary>
    bool VerifyWebhook(WebhookRequest request);

    /// <summary>Pulls the order reference and outcome out of a verified webhook body.</summary>
    GatewayOutcome ParseWebhook(string rawBody);

    /// <summary>
    /// Asks the gateway, over its own authenticated API, where an order stands. Null when this
    /// gateway cannot be asked (the sandbox, or none configured). As trustworthy as a signed
    /// webhook — we made the call ourselves, over TLS — which is what lets a payment whose webhook
    /// never arrived still credit.
    /// </summary>
    Task<GatewayOutcome?> QueryOrderAsync(string providerOrderId, CancellationToken cancellationToken = default);
}

/// <param name="ReturnUrl">Where the provider sends the browser afterwards. Built by ParkNest, never taken from a client.</param>
/// <param name="WebhookUrl">Where to call back, for providers that take it per order. Null when the API has no public address.</param>
/// <param name="ExpiresAt">When our own sweep gives up on the order, so the provider's expiry can match it.</param>
public sealed record GatewayOrderRequest(
    Guid OrderId,
    decimal Amount,
    string Currency,
    PaymentCustomer Customer,
    string ReturnUrl,
    string? WebhookUrl,
    DateTimeOffset ExpiresAt);

/// <summary>Who is paying. Some providers require a phone or an email on every order.</summary>
public sealed record PaymentCustomer(Guid UserId, string? Phone, string? Email, string? Name);

/// <param name="CheckoutPayload">
/// JSON for the client. Always carries a <c>checkout_url</c> to open, so no client needs a
/// provider's SDK; anything else in it is informational.
/// </param>
public sealed record GatewayOrder(string ProviderOrderId, string CheckoutPayload);

/// <summary>A webhook as received: the raw body, byte for byte as it was signed, and every header.</summary>
public sealed record WebhookRequest(string RawBody, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>A header's value, matched case-insensitively as HTTP requires; empty when absent.</summary>
    public string Header(string name)
    {
        if (Headers.TryGetValue(name, out var exact))
        {
            return exact;
        }

        foreach (var (key, value) in Headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return string.Empty;
    }
}

public enum GatewayOutcomeKind
{
    /// <summary>Nothing to apply yet: still at checkout, authorised but not captured, or an event we do not act on.</summary>
    Pending,

    /// <summary>Money received.</summary>
    Paid,

    Failed
}

/// <param name="ReportedAmount">
/// What the gateway says was paid. Never used to credit — the order record decides that — only as
/// a tripwire: a mismatch stops the credit and leaves the order for a human.
/// </param>
public sealed record GatewayOutcome(
    string ProviderOrderId,
    GatewayOutcomeKind Kind,
    string? ProviderPaymentId = null,
    string? FailureReason = null,
    decimal? ReportedAmount = null);
