using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Options;
using ParkNest.Application.Payments;
using ParkNest.Domain.Common;
using ParkNest.Domain.Payments;

namespace ParkNest.Infrastructure.Payments;

/// <summary>
/// Cashfree Payments, the recommended first real provider (docs/payment-gateway-setup-fully-free-rnd.md).
///
/// Three things differ from Razorpay, and each is why this is its own class rather than a
/// parameter on that one:
/// <list type="bullet">
///   <item>Cashfree's order id <em>is ours</em>. We name the order at creation, and every webhook
///   and status answer refers to it by that name — so <c>ProviderOrderId</c> is our own order id
///   as a string, and there is no second id to map.</item>
///   <item>Amounts cross the wire in rupees with two decimals, not paise.</item>
///   <item>The webhook signature covers the timestamp header and the body together, and is keyed
///   with the client secret: there is no separate webhook secret to configure.</item>
/// </list>
///
/// The payment page needs a <c>payment_session_id</c>, which is only valid for the order's life
/// and is not stored on our record. The hosted page asks Cashfree for it again when it renders,
/// which costs one API call per page load and saves a column that would be dead once the order
/// settles.
///
/// Cashfree requires the payer's mobile number on every order. An account signed in by email has
/// none, so the service can pass one collected at checkout; with neither, the order is refused
/// with <see cref="PaymentPhoneRequiredException"/> rather than sent with a made-up number that
/// Cashfree would use to look up saved instruments and send receipts.
/// </summary>
public sealed class CashfreePaymentGateway : IPaymentGateway, IHostedCheckoutPage
{
    public const string SignatureHeaderName = "x-webhook-signature";
    public const string TimestampHeaderName = "x-webhook-timestamp";

    /// <summary>Pinned: Cashfree versions its API by date, and a bump can change field names.</summary>
    public const string ApiVersion = "2026-01-01";

    private const string SandboxApiBase = "https://sandbox.cashfree.com/pg/";
    private const string LiveApiBase = "https://api.cashfree.com/pg/";
    private const string CheckoutScript = "https://sdk.cashfree.com/js/v3/cashfree.js";

    private readonly HttpClient _http;
    private readonly CashfreeGatewayOptions _options;
    private readonly bool _live;
    private readonly PaymentUrls _urls;
    private readonly ILogger<CashfreePaymentGateway> _logger;

    public CashfreePaymentGateway(
        HttpClient http,
        IOptions<PaymentOptions> options,
        PaymentUrls urls,
        ILogger<CashfreePaymentGateway> logger)
    {
        _http = http;
        _options = options.Value.Cashfree;
        _live = options.Value.IsLive;
        _urls = urls;
        _logger = logger;

        _http.DefaultRequestHeaders.Add("x-api-version", ApiVersion);
        _http.DefaultRequestHeaders.Add("x-client-id", _options.ClientId);
        _http.DefaultRequestHeaders.Add("x-client-secret", _options.ClientSecret);
    }

    public string Name => "Cashfree";

    /// <summary>The signature is an HMAC over timestamp + exact body, so a verified webhook proves its content.</summary>
    public bool WebhookIsAuthoritative => true;

    private string ApiBase => _live ? LiveApiBase : SandboxApiBase;

    public async Task<GatewayOrder> CreateOrderAsync(GatewayOrderRequest request, CancellationToken cancellationToken = default)
    {
        var phone = CustomerPhone(request.Customer.Phone)
                    ?? throw new PaymentPhoneRequiredException(Name);

        var meta = new Dictionary<string, object>
        {
            ["return_url"] = request.ReturnUrl
        };

        // Cashfree accepts https only here, and a webhook it cannot deliver is just a webhook we
        // never see — the return trip and the status question credit the order anyway.
        if (request.WebhookUrl is { } webhook && webhook.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            meta["notify_url"] = webhook;
        }

        if (!string.IsNullOrWhiteSpace(_options.PaymentMethods))
        {
            meta["payment_methods"] = _options.PaymentMethods.Trim();
        }

        var payload = new
        {
            // Our own id, which is what every webhook and status answer will then carry. A GUID
            // is 36 characters of hex and hyphens, inside Cashfree's 3–45 alphanumeric/-/_ rule.
            order_id = request.OrderId.ToString(),
            order_amount = Money.Round(request.Amount),
            order_currency = request.Currency,
            order_note = "ParkNest parking credits",
            customer_details = new
            {
                // 32 hex characters: inside the 3–50 alphanumeric rule, and stable per user.
                customer_id = request.Customer.UserId.ToString("N"),
                customer_phone = phone,
                customer_email = Clip(request.Customer.Email, 100),
                customer_name = Clip(request.Customer.Name, 100)
            },
            order_meta = meta,
            // Matched to our own sweep, so Cashfree stops taking money at about the moment we stop
            // expecting it. In UTC with a trailing Z: Cashfree's ISO 8601 parser accepts +05:30 but
            // rejects +00:00, which is what a zzz-formatted UTC offset produces.
            order_expiry_time = request.ExpiresAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
        };

        using var response = await _http.PostAsync(
            $"{ApiBase}orders",
            new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Cashfree's message names the offending field ("customer_phone : invalid"), which is
            // what an operator needs and a renter does not. Logged in full, surfaced generically.
            _logger.LogWarning(
                "Cashfree rejected order {OrderId} with {Status}: {Body}",
                request.OrderId, (int)response.StatusCode, body);

            throw new DomainException($"The payment gateway rejected the order ({(int)response.StatusCode}).");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var providerOrderId = root.TryGetProperty("order_id", out var id) ? id.GetString() : null;

        if (string.IsNullOrEmpty(providerOrderId))
        {
            throw new DomainException("The payment gateway returned no order id.");
        }

        var checkout = JsonSerializer.Serialize(new
        {
            provider = Name,
            order_id = providerOrderId,
            checkout_url = _urls.CheckoutUrl(providerOrderId),
            amount = Money.Round(request.Amount),
            currency = request.Currency
        });

        return new GatewayOrder(providerOrderId, checkout);
    }

    public bool VerifyWebhook(WebhookRequest request)
    {
        var signature = request.Header(SignatureHeaderName);
        var timestamp = request.Header(TimestampHeaderName);

        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(_options.ClientSecret))
        {
            return false;
        }

        // Base64(HMAC-SHA256(timestamp + raw body, client secret)). The timestamp is part of what
        // is signed, so a captured body cannot be resent under a fresh timestamp either.
        var computed = Sign(_options.ClientSecret, timestamp, request.RawBody);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(signature.Trim()));
    }

    public GatewayOutcome ParseWebhook(string rawBody)
    {
        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

        if (!root.TryGetProperty("data", out var data))
        {
            throw new DomainException("The webhook payload carried no data.");
        }

        var providerOrderId = data.TryGetProperty("order", out var order) && order.TryGetProperty("order_id", out var o)
            ? o.GetString()
            : null;

        if (string.IsNullOrEmpty(providerOrderId))
        {
            throw new DomainException("The webhook payload carried no order id.");
        }

        var hasPayment = data.TryGetProperty("payment", out var payment);
        var status = hasPayment && payment.TryGetProperty("payment_status", out var s) ? s.GetString() : null;

        // The event type says what Cashfree is telling us; the payment status says what happened.
        // Only when both say success is it money received. A dropped or otherwise unfinished
        // attempt leaves the order open, because the payment page lets the user try again.
        var kind = (type, status) switch
        {
            ("PAYMENT_SUCCESS_WEBHOOK", "SUCCESS") => GatewayOutcomeKind.Paid,
            ("PAYMENT_FAILED_WEBHOOK", _) => GatewayOutcomeKind.Failed,
            _ => GatewayOutcomeKind.Pending
        };

        var failureReason = kind == GatewayOutcomeKind.Failed
            ? (hasPayment && payment.TryGetProperty("payment_message", out var m) ? m.GetString() : null) ?? type
            : null;

        return new GatewayOutcome(
            providerOrderId,
            kind,
            hasPayment ? PaymentId(payment) : null,
            failureReason,
            ReportedAmount(order, hasPayment ? payment : null));
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

        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var payment in document.RootElement.EnumerateArray())
            {
                if (payment.TryGetProperty("payment_status", out var status) && status.GetString() == "SUCCESS")
                {
                    return new GatewayOutcome(
                        providerOrderId, GatewayOutcomeKind.Paid, PaymentId(payment), null, ReportedAmount(payment, payment));
                }
            }
        }

        // Nothing succeeded. A failed attempt is not a failed order — the page lets the user try
        // again — so it stays open for the expiry window rather than closing now.
        return new GatewayOutcome(providerOrderId, GatewayOutcomeKind.Pending);
    }

    public async Task<string> RenderAsync(PaymentOrder order, string returnUrl, CancellationToken cancellationToken = default)
    {
        var sessionId = await PaymentSessionIdAsync(order.ProviderOrderId, cancellationToken);

        var options = JsonSerializer.Serialize(new
        {
            mode = _live ? "production" : "sandbox",
            paymentSessionId = sessionId
        });

        var begin = $$"""
            const OPTIONS = {{options}};
            function begin() {
              // _self: Cashfree takes over this tab and sends it back to return_url afterwards.
              Cashfree({ mode: OPTIONS.mode }).checkout({ paymentSessionId: OPTIONS.paymentSessionId, redirectTarget: '_self' });
            }
            """;

        return HostedCheckoutPageHtml.Render(Name, order, returnUrl, CheckoutScript, begin);
    }

    private async Task<string> PaymentSessionIdAsync(string providerOrderId, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            $"{ApiBase}orders/{Uri.EscapeDataString(providerOrderId)}",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DomainException($"The payment gateway could not find the order ({(int)response.StatusCode}).");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        var sessionId = document.RootElement.TryGetProperty("payment_session_id", out var session)
            ? session.GetString()
            : null;

        return string.IsNullOrEmpty(sessionId)
            ? throw new DomainException("The payment gateway returned no payment session for the order.")
            : sessionId;
    }

    public static string Sign(string secret, string timestamp, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp + body)));
    }

    /// <summary>
    /// Cashfree wants ten digits for an Indian number, or a '+' and the country code for anything
    /// else. Ours are stored as bare digits, ten to fifteen of them, sometimes with the 91 in front.
    /// </summary>
    public static string? CustomerPhone(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        var digits = new string(stored.Where(char.IsDigit).ToArray());

        return digits.Length switch
        {
            10 => digits,
            12 when digits.StartsWith("91", StringComparison.Ordinal) => $"+{digits}",
            >= 11 and <= 15 => $"+{digits}",
            _ => null
        };
    }

    private static string? Clip(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];

    /// <summary>Cashfree sends the payment id as a string in some versions and a number in others.</summary>
    private static string? PaymentId(JsonElement payment) =>
        payment.TryGetProperty("cf_payment_id", out var id)
            ? id.ValueKind switch
            {
                JsonValueKind.String => id.GetString(),
                JsonValueKind.Number => id.GetRawText(),
                _ => null
            }
            : null;

    /// <summary>
    /// What Cashfree believes the order was for. Preferred over the payment's own amount, which
    /// can differ by a surcharge the payer was charged on top — the tripwire in settlement is
    /// about our order and theirs agreeing, not about fees.
    /// </summary>
    private static decimal? ReportedAmount(JsonElement order, JsonElement? payment)
    {
        if (order.ValueKind == JsonValueKind.Object
            && order.TryGetProperty("order_amount", out var orderAmount)
            && orderAmount.ValueKind == JsonValueKind.Number)
        {
            return orderAmount.GetDecimal();
        }

        if (payment is { } p
            && p.TryGetProperty("payment_amount", out var paymentAmount)
            && paymentAmount.ValueKind == JsonValueKind.Number)
        {
            return paymentAmount.GetDecimal();
        }

        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
