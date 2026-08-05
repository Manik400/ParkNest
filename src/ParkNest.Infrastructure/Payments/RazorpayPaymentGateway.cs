using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ParkNest.Application.Options;
using ParkNest.Application.Payments;
using ParkNest.Domain.Common;

namespace ParkNest.Infrastructure.Payments;

/// <summary>
/// Razorpay, chosen per PRD §14.1 for its India-first UPI support.
///
/// Amounts cross the wire in paise (the smallest currency unit), which is the usual source of
/// hundred-fold errors in this kind of integration, so the conversion is done in exactly one place.
/// </summary>
public sealed class RazorpayPaymentGateway : IPaymentGateway
{
    private const string OrdersEndpoint = "https://api.razorpay.com/v1/orders";

    private readonly HttpClient _http;
    private readonly PaymentOptions _options;

    public RazorpayPaymentGateway(HttpClient http, IOptions<PaymentOptions> options)
    {
        _http = http;
        _options = options.Value;

        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_options.KeyId}:{_options.KeySecret}"));

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    public string Name => "Razorpay";

    public string SignatureHeader => "X-Razorpay-Signature";

    public async Task<GatewayOrder> CreateOrderAsync(
        Guid orderId,
        decimal amount,
        string currency,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            // Paise. Rounded before multiplying so a fractional credit cannot smuggle in a
            // sub-paise remainder the gateway would silently truncate.
            amount = (long)(Money.Round(amount) * 100m),
            currency,
            // Our own id travels with the order so a webhook can always be traced back.
            receipt = orderId.ToString(),
            notes = new { parknest_order_id = orderId.ToString() }
        };

        using var response = await _http.PostAsJsonAsync(OrdersEndpoint, payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DomainException($"The payment gateway rejected the order ({(int)response.StatusCode}).");
        }

        using var document = JsonDocument.Parse(body);
        var providerOrderId = document.RootElement.GetProperty("id").GetString()
                              ?? throw new DomainException("The payment gateway returned no order id.");

        // Everything the browser SDK needs to open the checkout sheet. The key id is public by
        // design; the secret never leaves the server.
        var checkout = JsonSerializer.Serialize(new
        {
            key = _options.KeyId,
            order_id = providerOrderId,
            amount = (long)(Money.Round(amount) * 100m),
            currency,
            name = "ParkNest",
            description = "Parking credits"
        });

        return new GatewayOrder(providerOrderId, checkout);
    }

    public bool VerifyWebhookSignature(string rawBody, string signature)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(_options.WebhookSecret))
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
        var computed = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody)))
            .ToLowerInvariant();

        // Fixed-time compare: a short-circuiting string equality leaks the expected signature one
        // byte at a time to anyone willing to measure the response.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant()));
    }

    public WebhookEvent ParseWebhook(string rawBody)
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
        // still fall through.
        var succeeded = eventName == "payment.captured";

        var failureReason = entity.TryGetProperty("error_description", out var reason)
            ? reason.GetString()
            : eventName;

        return new WebhookEvent(providerOrderId, providerPaymentId, succeeded, succeeded ? null : failureReason);
    }
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
