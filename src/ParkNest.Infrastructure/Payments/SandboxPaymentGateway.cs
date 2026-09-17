using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ParkNest.Application.Options;
using ParkNest.Application.Payments;
using ParkNest.Domain.Common;

namespace ParkNest.Infrastructure.Payments;

/// <summary>
/// A payment gateway that runs inside this process. It moves no money — it exists so the whole
/// recharge path (order → hosted checkout → signed webhook → verification → credits) can be built,
/// demonstrated and regression-tested with no merchant account, no credentials, and no network.
///
/// There is no such thing as an open-source gateway that settles real funds; that requires a
/// licensed PSP. So this deliberately does not pretend to be one. What it does do is reproduce the
/// <em>protocol</em> faithfully — same HMAC-SHA256-over-raw-body scheme, same event envelope shape,
/// same capture-versus-authorize distinction as Razorpay — so that switching
/// <c>Payments:Provider</c> to a real gateway changes the edge of the system and nothing else.
///
/// It is refused in Production at startup. Anything that can mint a valid signature for its own
/// webhooks can mint credits, and this class hands that ability to whoever opens the checkout page.
/// </summary>
public sealed class SandboxPaymentGateway : IPaymentGateway
{
    public const string SignatureHeaderName = "X-ParkNest-Sandbox-Signature";

    private readonly byte[] _secret;

    public SandboxPaymentGateway(IOptions<PaymentOptions> options)
    {
        var configured = options.Value.Sandbox.WebhookSecret;

        // A configured secret makes signatures stable across restarts, which is what you want when
        // replaying a captured webhook body from a log. Without one, a per-process random key is
        // the safer default: nothing to leak into source control, and an abandoned checkout page
        // stops being replayable the moment the server restarts.
        _secret = string.IsNullOrWhiteSpace(configured)
            ? RandomNumberGenerator.GetBytes(32)
            : Encoding.UTF8.GetBytes(configured);
    }

    public string Name => "Sandbox";

    public bool WebhookIsAuthoritative => true;

    public Task<GatewayOrder> CreateOrderAsync(GatewayOrderRequest request, CancellationToken cancellationToken = default)
    {
        // Prefixed and random, like a real provider id, so nothing downstream can quietly come to
        // depend on it being our own order id in disguise.
        var providerOrderId = $"sbx_order_{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";

        var checkout = JsonSerializer.Serialize(new
        {
            provider = Name,
            order_id = providerOrderId,
            // Relative, so it works unchanged whichever host or port the API is bound to.
            checkout_url = $"/sandbox/checkout/{providerOrderId}",
            amount = Money.Round(request.Amount),
            currency = request.Currency,
            name = "ParkNest",
            description = "Parking credits"
        });

        return Task.FromResult(new GatewayOrder(providerOrderId, checkout));
    }

    public bool VerifyWebhook(WebhookRequest request)
    {
        var signature = request.Header(SignatureHeaderName);

        if (string.IsNullOrEmpty(signature))
        {
            return false;
        }

        var expected = Sign(request.RawBody);

        // Fixed-time compare, same as the real gateway. The sandbox is where this code gets
        // exercised most, so it must not be the version that teaches the wrong habit.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant()));
    }

    public GatewayOutcome ParseWebhook(string rawBody)
    {
        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;

        var eventName = root.TryGetProperty("event", out var e) ? e.GetString() : null;

        var entity = root
            .GetProperty("payload")
            .GetProperty("payment")
            .GetProperty("entity");

        var providerOrderId = entity.TryGetProperty("order_id", out var o) ? o.GetString() : null;
        var providerPaymentId = entity.TryGetProperty("id", out var p) ? p.GetString() : null;

        if (string.IsNullOrEmpty(providerOrderId))
        {
            throw new DomainException("The webhook payload carried no order id.");
        }

        // Only a capture is money received — "authorized" funds can still fall through, so they
        // are nothing to act on yet. Mirrored from Razorpay on purpose, so a test written against
        // the sandbox proves something about the real thing.
        var kind = eventName switch
        {
            "payment.captured" => GatewayOutcomeKind.Paid,
            "payment.failed" => GatewayOutcomeKind.Failed,
            _ => GatewayOutcomeKind.Pending
        };

        var failureReason = kind == GatewayOutcomeKind.Failed
            ? entity.TryGetProperty("error_description", out var reason) ? reason.GetString() : eventName
            : null;

        return new GatewayOutcome(providerOrderId, kind, providerPaymentId, failureReason);
    }

    /// <summary>
    /// Nothing to ask: the sandbox's only record of a payment is the callback its own checkout page
    /// sends. The return trip and the sweep find nothing here, which is the truth.
    /// </summary>
    public Task<GatewayOutcome?> QueryOrderAsync(string providerOrderId, CancellationToken cancellationToken = default) =>
        Task.FromResult<GatewayOutcome?>(null);

    /// <summary>
    /// Builds the callback a real gateway would send, signed with the sandbox secret. This is the
    /// half a hosted checkout would do on the provider's servers; here it is the checkout page's
    /// "Pay" button.
    /// </summary>
    public SignedWebhook BuildWebhook(string providerOrderId, bool succeeded, string? failureReason = null)
    {
        var paymentId = $"sbx_pay_{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";

        var body = JsonSerializer.Serialize(new
        {
            @event = succeeded ? "payment.captured" : "payment.failed",
            payload = new
            {
                payment = new
                {
                    entity = new
                    {
                        id = paymentId,
                        order_id = providerOrderId,
                        error_description = succeeded ? null : failureReason ?? "Declined in the sandbox checkout."
                    }
                }
            }
        });

        // Sign the exact bytes that will be transmitted. Re-serialising anywhere between here and
        // the wire would change the whitespace and invalidate the signature — which is precisely
        // the failure this arrangement is meant to make impossible to introduce by accident.
        return new SignedWebhook(body, Sign(body), SignatureHeaderName);
    }

    private string Sign(string body)
    {
        using var hmac = new HMACSHA256(_secret);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }
}

public sealed record SignedWebhook(string Body, string Signature, string HeaderName);
