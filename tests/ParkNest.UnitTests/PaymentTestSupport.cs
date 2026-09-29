using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Application.Payments;
using ParkNest.Infrastructure.Payments;

namespace ParkNest.UnitTests;

/// <summary>The payment service wired the way the container wires it, around whichever gateway a test needs.</summary>
internal static class PaymentTestSupport
{
    public static (IPaymentService Payments, IPaymentReconciler Reconciler) Create(
        TestHarness h,
        IPaymentGateway gateway,
        PaymentOptions? options = null)
    {
        var wrapped = Microsoft.Extensions.Options.Options.Create(options ?? new PaymentOptions());

        var settlement = new PaymentSettlement(
            h.Db, h.Wallets, h.Analytics, h.Clock, NullLogger<PaymentSettlement>.Instance);
        var reconciler = new PaymentReconciler(
            h.Db, gateway, settlement, h.Clock, wrapped, NullLogger<PaymentReconciler>.Instance);

        var payments = new PaymentService(
            h.Db, gateway, settlement, reconciler, h.Analytics, new PaymentUrls(wrapped),
            h.CurrentUser, h.Clock,
            Microsoft.Extensions.Options.Options.Create(h.Options), wrapped,
            NullLogger<PaymentService>.Instance);

        return (payments, reconciler);
    }

    public static GatewayOrderRequest SampleRequest(decimal amount = 500m) => new(
        Guid.NewGuid(),
        amount,
        "INR",
        new PaymentCustomer(Guid.NewGuid(), "9876543210", "renter@example.com", "A Renter"),
        "https://api.test/checkout/return/x",
        "https://api.test/api/payments/webhook",
        DateTimeOffset.UtcNow.AddMinutes(30));

    public static WebhookRequest ToRequest(this SignedWebhook webhook) =>
        new(webhook.Body, new Dictionary<string, string> { [webhook.HeaderName] = webhook.Signature });

    public static WebhookRequest Unsigned(string body) => new(body, new Dictionary<string, string>());
}

/// <summary>
/// Mirrors the real gateways' HMAC-SHA256-over-raw-body scheme without touching the network, and
/// lets a test script what the gateway answers when asked about an order.
/// </summary>
internal sealed class FakeGateway : IPaymentGateway
{
    public const string SignatureHeaderName = "X-Fake-Signature";

    private readonly string _secret;
    private int _counter;

    public FakeGateway(string secret, bool webhookIsAuthoritative = true)
    {
        _secret = secret;
        WebhookIsAuthoritative = webhookIsAuthoritative;
    }

    public string Name => "Fake";

    public bool WebhookIsAuthoritative { get; }

    /// <summary>What the gateway says when asked about an order. Null (the default) means it cannot be asked.</summary>
    public Func<string, GatewayOutcome?>? Status { get; set; }

    /// <summary>How many times the gateway has been asked — what the reconcile throttle is measured by.</summary>
    public int StatusQueries { get; private set; }

    public GatewayOrderRequest? LastRequest { get; private set; }

    public Task<GatewayOrder> CreateOrderAsync(GatewayOrderRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(new GatewayOrder($"order_{++_counter}", "{}"));
    }

    public bool VerifyWebhook(WebhookRequest request)
    {
        var signature = request.Header(SignatureHeaderName);

        if (string.IsNullOrEmpty(signature))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Sign(_secret, request.RawBody)),
            Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant()));
    }

    public GatewayOutcome ParseWebhook(string rawBody)
    {
        using var document = System.Text.Json.JsonDocument.Parse(rawBody);
        var root = document.RootElement;

        var kind = root.GetProperty("event").GetString() switch
        {
            "payment.captured" => GatewayOutcomeKind.Paid,
            "payment.failed" => GatewayOutcomeKind.Failed,
            _ => GatewayOutcomeKind.Pending
        };

        decimal? amount = root.TryGetProperty("amount", out var a) ? a.GetDecimal() : null;

        return new GatewayOutcome(
            root.GetProperty("order").GetString()!,
            kind,
            root.GetProperty("payment").GetString(),
            kind == GatewayOutcomeKind.Failed ? "Declined." : null,
            amount);
    }

    public Task<GatewayOutcome?> QueryOrderAsync(string providerOrderId, CancellationToken cancellationToken = default)
    {
        StatusQueries++;
        return Task.FromResult(Status?.Invoke(providerOrderId));
    }

    public static string Sign(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }
}

/// <summary>
/// Answers HTTP calls from a queue and records what was sent, so a gateway adapter can be driven
/// through its real request-building and response-parsing code with no network.
/// </summary>
internal sealed class RecordingHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<RecordedRequest> Requests { get; } = new();

    public RecordingHttpHandler Respond(HttpStatusCode status, string body)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        return this;
    }

    public RecordingHttpHandler Fail(Exception exception)
    {
        _responses.Enqueue(_ => throw exception);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            body,
            request.Headers.Authorization?.ToString(),
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException($"No response queued for {request.Method} {request.RequestUri}.");
        }

        return _responses.Dequeue()(request);
    }
}

internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string? Body,
    string? Authorization,
    IReadOnlyDictionary<string, string> Headers);
