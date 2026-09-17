using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using ParkNest.Application.Options;
using ParkNest.Application.Payments;
using ParkNest.Domain.Common;
using ParkNest.Domain.Payments;
using ParkNest.Infrastructure.Payments;

namespace ParkNest.UnitTests;

/// <summary>
/// The Razorpay adapter, driven through its real request-building and parsing with no network.
/// What it has to get right is the boundary: paise on the wire, a signature over the exact body,
/// only a capture counting as money, and a status answer that credits a payment whose webhook
/// never came.
/// </summary>
public sealed class RazorpayPaymentGatewayTests
{
    private const string KeyId = "rzp_test_abc";
    private const string KeySecret = "key-secret";
    private const string WebhookSecret = "webhook-secret";

    private static RazorpayPaymentGateway Gateway(RecordingHttpHandler handler)
    {
        var options = Options.Create(new PaymentOptions
        {
            Provider = "Razorpay",
            PublicBaseUrl = "https://api.test",
            Razorpay = new RazorpayGatewayOptions { KeyId = KeyId, KeySecret = KeySecret, WebhookSecret = WebhookSecret }
        });

        return new RazorpayPaymentGateway(new HttpClient(handler), options, new PaymentUrls(options));
    }

    private static string Sign(string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    private static string PaymentEvent(string eventName, long amountPaise = 50050) =>
        "{\"event\":\"" + eventName + "\",\"payload\":{\"payment\":{\"entity\":{" +
        "\"id\":\"pay_1\",\"order_id\":\"order_123\",\"amount\":" + amountPaise +
        ",\"error_description\":\"Bank declined.\"}}}}";

    [Fact]
    public async Task Creates_the_order_in_paise_with_basic_auth_and_points_the_client_at_our_checkout_page()
    {
        var handler = new RecordingHttpHandler().Respond(HttpStatusCode.OK, """{"id":"order_123"}""");

        var order = await Gateway(handler).CreateOrderAsync(PaymentTestSupport.SampleRequest(500.50m));

        var sent = handler.Requests.Single();
        sent.Uri.ToString().Should().Be("https://api.razorpay.com/v1/orders");
        sent.Authorization.Should().Be($"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{KeyId}:{KeySecret}"))}");

        using var body = JsonDocument.Parse(sent.Body!);
        body.RootElement.GetProperty("amount").GetInt64().Should().Be(50050, "Razorpay takes paise");

        order.ProviderOrderId.Should().Be("order_123");
        using var payload = JsonDocument.Parse(order.CheckoutPayload);
        payload.RootElement.GetProperty("checkout_url").GetString().Should().Be("https://api.test/checkout/order_123");
    }

    [Fact]
    public async Task A_rejected_order_is_a_domain_error()
    {
        var handler = new RecordingHttpHandler().Respond(HttpStatusCode.BadRequest, """{"error":{"description":"bad key"}}""");

        var act = () => Gateway(handler).CreateOrderAsync(PaymentTestSupport.SampleRequest());

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public void A_signature_over_the_exact_body_verifies_whatever_the_header_case()
    {
        var gateway = Gateway(new RecordingHttpHandler());
        var body = PaymentEvent("payment.captured");

        gateway.VerifyWebhook(new WebhookRequest(body, new Dictionary<string, string>
        {
            ["x-razorpay-signature"] = Sign(body)
        })).Should().BeTrue();
    }

    [Fact]
    public void A_tampered_body_or_a_missing_signature_fails()
    {
        var gateway = Gateway(new RecordingHttpHandler());
        var body = PaymentEvent("payment.captured");

        gateway.VerifyWebhook(new WebhookRequest(body.Replace("50050", "99999"), new Dictionary<string, string>
        {
            [RazorpayPaymentGateway.SignatureHeaderName] = Sign(body)
        })).Should().BeFalse();

        gateway.VerifyWebhook(PaymentTestSupport.Unsigned(body)).Should().BeFalse();
    }

    [Fact]
    public void Only_a_capture_is_money_received()
    {
        var gateway = Gateway(new RecordingHttpHandler());

        var captured = gateway.ParseWebhook(PaymentEvent("payment.captured"));
        captured.Kind.Should().Be(GatewayOutcomeKind.Paid);
        captured.ProviderOrderId.Should().Be("order_123");
        captured.ReportedAmount.Should().Be(500.50m);

        gateway.ParseWebhook(PaymentEvent("payment.authorized")).Kind.Should().Be(GatewayOutcomeKind.Pending);

        var failed = gateway.ParseWebhook(PaymentEvent("payment.failed"));
        failed.Kind.Should().Be(GatewayOutcomeKind.Failed);
        failed.FailureReason.Should().Be("Bank declined.");
    }

    [Fact]
    public async Task Asked_about_an_order_it_reports_paid_when_any_attempt_was_captured()
    {
        var handler = new RecordingHttpHandler().Respond(HttpStatusCode.OK, """
            {"items":[{"id":"pay_0","status":"failed","amount":50000},{"id":"pay_1","status":"captured","amount":50000}]}
            """);

        var outcome = await Gateway(handler).QueryOrderAsync("order_123");

        handler.Requests.Single().Uri.ToString().Should().Be("https://api.razorpay.com/v1/orders/order_123/payments");
        outcome!.Kind.Should().Be(GatewayOutcomeKind.Paid);
        outcome.ProviderPaymentId.Should().Be("pay_1");
        outcome.ReportedAmount.Should().Be(500m);
    }

    [Fact]
    public async Task Asked_about_an_order_with_only_failed_attempts_it_leaves_it_open()
    {
        // The sheet allows a retry, so a failed attempt is not a failed order.
        var handler = new RecordingHttpHandler().Respond(HttpStatusCode.OK, """
            {"items":[{"id":"pay_0","status":"failed","amount":50000}]}
            """);

        (await Gateway(handler).QueryOrderAsync("order_123"))!.Kind.Should().Be(GatewayOutcomeKind.Pending);
    }

    [Fact]
    public async Task The_checkout_page_opens_razorpay_and_returns_to_our_endpoint()
    {
        var order = new PaymentOrder { ProviderOrderId = "order_123", Amount = 500.50m, Currency = "INR" };
        var returnUrl = $"https://api.test/checkout/return/{order.Id}";

        var html = await Gateway(new RecordingHttpHandler()).RenderAsync(order, returnUrl);

        html.Should().Contain("https://checkout.razorpay.com/v1/checkout.js");
        html.Should().Contain("\"order_id\":\"order_123\"");
        html.Should().Contain("\"amount\":50050");
        html.Should().Contain($"\"callback_url\":\"{returnUrl}\"");
        html.Should().Contain("\"redirect\":true");
        html.Should().NotContain(KeySecret, "the secret never leaves the server");
    }
}
