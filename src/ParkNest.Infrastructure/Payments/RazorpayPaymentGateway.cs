using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ParkNest.Application.Options;
using ParkNest.Application.Payments;
using ParkNest.Domain.Common;
using ParkNest.Domain.Payments;

namespace ParkNest.Infrastructure.Payments;

/// <summary>
/// Razorpay, chosen per PRD §14.1 for its India-first UPI support.
///
/// Amounts cross the wire in paise (the smallest currency unit), which is the usual source of
/// hundred-fold errors in this kind of integration, so the conversion is done in exactly one place.
///
/// Razorpay's payment sheet opens only from its <c>checkout.js</c>, so orders point the client at a
/// page this API serves (<see cref="IHostedCheckoutPage"/>) that loads it with
/// <c>redirect: true</c> — Razorpay then sends the browser to our return endpoint, which asks
/// Razorpay where the payment stands. Enable auto-capture in the dashboard: an uncaptured payment
/// is only "authorized", never credits, and is refunded automatically.
/// </summary>
public sealed class RazorpayPaymentGateway : IPaymentGateway, IHostedCheckoutPage
{
    public const string SignatureHeaderName = "X-Razorpay-Signature";

    private const string ApiBase = "https://api.razorpay.com/v1/";
    private const string CheckoutScript = "https://checkout.razorpay.com/v1/checkout.js";

    private readonly HttpClient _http;
    private readonly RazorpayGatewayOptions _options;
    private readonly PaymentUrls _urls;

    public RazorpayPaymentGateway(HttpClient http, IOptions<PaymentOptions> options, PaymentUrls urls)
    {
        _http = http;
        _options = options.Value.Razorpay;
        _urls = urls;

        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_options.KeyId}:{_options.KeySecret}"));

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    public string Name => "Razorpay";

    /// <summary>The signature is an HMAC over the exact body, so a verified webhook proves its content.</summary>
    public bool WebhookIsAuthoritative => true;

    public async Task<GatewayOrder> CreateOrderAsync(GatewayOrderRequest request, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            amount = ToPaise(request.Amount),
            currency = request.Currency,
            // Our own id travels with the order so a webhook can always be traced back.
            receipt = request.OrderId.ToString(),
            notes = new { parknest_order_id = request.OrderId.ToString() }
        };

        using var response = await _http.PostAsJsonAsync($"{ApiBase}orders", payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DomainException($"The payment gateway rejected the order ({(int)response.StatusCode}).");
        }

        using var document = JsonDocument.Parse(body);
        var providerOrderId = document.RootElement.GetProperty("id").GetString()
                              ?? throw new DomainException("The payment gateway returned no order id.");

        var checkout = JsonSerializer.Serialize(new
        {
            provider = Name,
            order_id = providerOrderId,
            checkout_url = _urls.CheckoutUrl(providerOrderId),
            // What checkout.js needs, for a client that would rather open the sheet itself. The key
            // id is public by design; the secret never leaves the server.
            key = _options.KeyId,
            amount = ToPaise(request.Amount),
            currency = request.Currency
        });

        return new GatewayOrder(providerOrderId, checkout);
    }

    public bool VerifyWebhook(WebhookRequest request)
    {
        var signature = request.Header(SignatureHeaderName);

        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(_options.WebhookSecret))
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
        var computed = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(request.RawBody)))
            .ToLowerInvariant();

        // Fixed-time compare: a short-circuiting string equality leaks the expected signature one
        // byte at a time to anyone willing to measure the response.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
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

        // Only an explicit capture counts as money received. "authorized" means the funds are
        // merely held, and treating it as success would issue credits for a payment that can
        // still fall through — so it, like any event we do not act on, is Pending.
        var kind = eventName switch
        {
            "payment.captured" or "order.paid" => GatewayOutcomeKind.Paid,
            "payment.failed" => GatewayOutcomeKind.Failed,
            _ => GatewayOutcomeKind.Pending
        };

        var failureReason = kind == GatewayOutcomeKind.Failed
            ? entity.TryGetProperty("error_description", out var reason) ? reason.GetString() : eventName
            : null;

        return new GatewayOutcome(providerOrderId, kind, providerPaymentId, failureReason, ReportedAmount(entity));
    }

    public async Task<GatewayOutcome?> QueryOrderAsync(string providerOrderId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(
            $"{ApiBase}orders/{Uri.EscapeDataString(providerOrderId)}/payments",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DomainException($"The payment gateway could not report on the order ({(int)response.StatusCode}).");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        if (document.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var payment in items.EnumerateArray())
            {
                if (payment.TryGetProperty("status", out var status) && status.GetString() == "captured")
                {
                    var paymentId = payment.TryGetProperty("id", out var id) ? id.GetString() : null;
                    return new GatewayOutcome(
                        providerOrderId, GatewayOutcomeKind.Paid, paymentId, null, ReportedAmount(payment));
                }
            }
        }

        // Nothing captured. A failed attempt is not a failed order — the sheet lets the user try
        // again — so it is left open for the expiry window rather than closed now.
        return new GatewayOutcome(providerOrderId, GatewayOutcomeKind.Pending);
    }

    public Task<string> RenderAsync(PaymentOrder order, string returnUrl, CancellationToken cancellationToken = default)
    {
        var options = JsonSerializer.Serialize(new
        {
            key = _options.KeyId,
            order_id = order.ProviderOrderId,
            amount = ToPaise(order.Amount),
            currency = order.Currency,
            name = "ParkNest",
            description = "Parking credits",
            // Razorpay posts the browser here once the payment is done. What it posts is ignored;
            // the return endpoint asks Razorpay itself.
            callback_url = returnUrl,
            redirect = true
        });

        var begin = $$"""
            const OPTIONS = {{options}};
            function begin() {
              OPTIONS.modal = { ondismiss: () => { window.location.href = RETURN_URL; } };
              new Razorpay(OPTIONS).open();
            }
            """;

        return Task.FromResult(HostedCheckoutPageHtml.Render(Name, order, returnUrl, CheckoutScript, begin));
    }

    private static long ToPaise(decimal amount) =>
        // Rounded before multiplying so a fractional credit cannot smuggle in a sub-paise remainder
        // the gateway would silently truncate.
        (long)(Money.Round(amount) * 100m);

    private static decimal? ReportedAmount(JsonElement payment) =>
        payment.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.Number
            ? amount.GetInt64() / 100m
            : null;
}

file static class HttpClientJsonExtensions
{
    public static Task<HttpResponseMessage> PostAsJsonAsync<T>(
        this HttpClient client, string url, T value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return client.PostAsync(url, content, cancellationToken);
    }
}
