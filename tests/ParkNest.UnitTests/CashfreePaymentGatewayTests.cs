using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ParkNest.Application.Options;
using ParkNest.Application.Payments;
using ParkNest.Domain.Common;
using ParkNest.Domain.Payments;
using ParkNest.Infrastructure.Payments;

namespace ParkNest.UnitTests;

/// <summary>
/// The Cashfree adapter, driven through its real request-building and parsing with no network.
/// What it has to get right is the boundary: our own id as the order name, rupees not paise, a
/// signature over timestamp + body keyed with the client secret, only a SUCCESS counting as money,
/// and a refusal — not a made-up number — when the account has no phone.
/// </summary>
public sealed class CashfreePaymentGatewayTests
{
    private const string ClientId = "TEST1234567890";
    private const string ClientSecret = "cfsk_ma_test_secret";

    private static CashfreePaymentGateway Gateway(RecordingHttpHandler handler, bool live = false, string paymentMethods = "")
    {
        var options = Options.Create(new PaymentOptions
        {
            Provider = "Cashfree",
            Mode = live ? "Live" : "Test",
            PublicBaseUrl = "https://api.test",
            Cashfree = new CashfreeGatewayOptions { ClientId = ClientId, ClientSecret = ClientSecret, PaymentMethods = paymentMethods }
        });

        return new CashfreePaymentGateway(
            new HttpClient(handler), options, new PaymentUrls(options), NullLogger<CashfreePaymentGateway>.Instance);
    }

    private static string Webhook(string type, string status, decimal orderAmount = 500.50m, decimal? paymentAmount = null) =>
        "{\"data\":{\"order\":{\"order_id\":\"11111111-2222-3333-4444-555555555555\",\"order_amount\":" +
        orderAmount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ",\"order_currency\":\"INR\"},\"payment\":{\"cf_payment_id\":5114933189368,\"payment_status\":\"" + status +
        "\",\"payment_amount\":" + (paymentAmount ?? orderAmount).ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ",\"payment_message\":\"Bank declined.\"}},\"event_time\":\"2026-09-12T10:00:00+05:30\",\"type\":\"" + type + "\"}";

    private static WebhookRequest Signed(string body, string timestamp = "1757650000000") =>
        new(body, new Dictionary<string, string>
        {
            ["X-Webhook-Timestamp"] = timestamp,
            ["X-Webhook-Signature"] = CashfreePaymentGateway.Sign(ClientSecret, timestamp, body)
        });

    [Fact]
    public async Task Creates_the_order_under_our_own_id_in_rupees_and_points_the_client_at_our_checkout_page()
    {
        var handler = new RecordingHttpHandler().Respond(HttpStatusCode.OK,
            """{"cf_order_id":"2183","order_id":"will-be-echoed","payment_session_id":"session_abc","order_status":"ACTIVE"}""");
        var request = PaymentTestSupport.SampleRequest(500.50m);

        var order = await Gateway(handler).CreateOrderAsync(request);

        var sent = handler.Requests.Single();
        sent.Uri.ToString().Should().Be("https://sandbox.cashfree.com/pg/orders", "Mode=Test is the sandbox host");
        sent.Headers["x-client-id"].Should().Be(ClientId);
        sent.Headers["x-client-secret"].Should().Be(ClientSecret);
        sent.Headers["x-api-version"].Should().Be(CashfreePaymentGateway.ApiVersion);

        using var body = JsonDocument.Parse(sent.Body!);
        var root = body.RootElement;
        root.GetProperty("order_id").GetString().Should().Be(request.OrderId.ToString(), "Cashfree names the order what we tell it");
        root.GetProperty("order_amount").GetDecimal().Should().Be(500.50m, "Cashfree takes rupees, not paise");
        root.GetProperty("order_currency").GetString().Should().Be("INR");
        root.GetProperty("customer_details").GetProperty("customer_phone").GetString().Should().Be("9876543210");
        root.GetProperty("customer_details").GetProperty("customer_id").GetString().Should().Be(request.Customer.UserId.ToString("N"));
        root.GetProperty("order_meta").GetProperty("return_url").GetString().Should().Be(request.ReturnUrl);
        root.GetProperty("order_meta").GetProperty("notify_url").GetString().Should().Be(request.WebhookUrl);
        root.GetProperty("order_meta").TryGetProperty("payment_methods", out _).Should().BeFalse("nothing was configured");
        root.GetProperty("order_expiry_time").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", "UTC with a Z; Cashfree rejects +00:00");

        order.ProviderOrderId.Should().Be("will-be-echoed");
        using var payload = JsonDocument.Parse(order.CheckoutPayload);
        payload.RootElement.GetProperty("checkout_url").GetString().Should().Be("https://api.test/checkout/will-be-echoed");
        payload.RootElement.GetProperty("provider").GetString().Should().Be("Cashfree");
    }

    [Fact]
    public async Task Live_mode_talks_to_the_production_host_and_a_method_filter_is_passed_through()
    {
        var handler = new RecordingHttpHandler().Respond(HttpStatusCode.OK, """{"order_id":"x","payment_session_id":"s"}""");

        await Gateway(handler, live: true, paymentMethods: "upi").CreateOrderAsync(PaymentTestSupport.SampleRequest());

        var sent = handler.Requests.Single();
        sent.Uri.ToString().Should().Be("https://api.cashfree.com/pg/orders");
        using var body = JsonDocument.Parse(sent.Body!);
        body.RootElement.GetProperty("order_meta").GetProperty("payment_methods").GetString().Should().Be("upi");
    }

    [Fact]
    public async Task An_http_webhook_url_is_left_out_because_cashfree_takes_https_only()
    {
        var handler = new RecordingHttpHandler().Respond(HttpStatusCode.OK, """{"order_id":"x","payment_session_id":"s"}""");
        var request = PaymentTestSupport.SampleRequest() with { WebhookUrl = "http://localhost:5000/api/payments/webhook" };

        await Gateway(handler).CreateOrderAsync(request);

        using var body = JsonDocument.Parse(handler.Requests.Single().Body!);
        body.RootElement.GetProperty("order_meta").TryGetProperty("notify_url", out _).Should().BeFalse();
    }

    [Fact]
    public async Task No_phone_on_the_account_refuses_the_order_rather_than_inventing_one()
    {
        var handler = new RecordingHttpHandler();
        var request = PaymentTestSupport.SampleRequest() with
        {
            Customer = new PaymentCustomer(Guid.NewGuid(), null, "renter@example.com", "A Renter")
        };

        var act = () => Gateway(handler).CreateOrderAsync(request);

        await act.Should().ThrowAsync<PaymentPhoneRequiredException>();
        handler.Requests.Should().BeEmpty("nothing was sent");
    }

    [Theory]
    [InlineData("9876543210", "9876543210")]
    [InlineData("+91 98765 43210", "+919876543210")]
    [InlineData("919876543210", "+919876543210")]
    [InlineData("447700900123", "+447700900123")]
    [InlineData("12345", null)]
    [InlineData("", null)]
    public void Phones_are_shaped_the_way_cashfree_wants_them(string stored, string? expected)
    {
        CashfreePaymentGateway.CustomerPhone(stored).Should().Be(expected);
    }

    [Fact]
    public async Task A_rejected_order_is_a_domain_error()
    {
        var handler = new RecordingHttpHandler().Respond(HttpStatusCode.Unauthorized, """{"message":"authentication failed"}""");

        var act = () => Gateway(handler).CreateOrderAsync(PaymentTestSupport.SampleRequest());

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public void A_signature_over_timestamp_and_body_keyed_with_the_client_secret_verifies_whatever_the_header_case()
    {
        var gateway = Gateway(new RecordingHttpHandler());

        gateway.VerifyWebhook(Signed(Webhook("PAYMENT_SUCCESS_WEBHOOK", "SUCCESS"))).Should().BeTrue();
    }

    [Fact]
    public void A_tampered_body_a_changed_timestamp_or_a_missing_header_fails()
    {
        var gateway = Gateway(new RecordingHttpHandler());
        var body = Webhook("PAYMENT_SUCCESS_WEBHOOK", "SUCCESS");
        var signed = Signed(body);

        gateway.VerifyWebhook(signed with { RawBody = body.Replace("500.5", "9999") }).Should().BeFalse();

        var replayedUnderNewTimestamp = new WebhookRequest(body, new Dictionary<string, string>
        {
            ["x-webhook-timestamp"] = "1757659999999",
            ["x-webhook-signature"] = signed.Headers["X-Webhook-Signature"]
        });
        gateway.VerifyWebhook(replayedUnderNewTimestamp).Should().BeFalse("the timestamp is part of what was signed");

        gateway.VerifyWebhook(new WebhookRequest(body, new Dictionary<string, string>
        {
            ["x-webhook-signature"] = signed.Headers["X-Webhook-Signature"]
        })).Should().BeFalse("no timestamp, nothing to check against");

        gateway.VerifyWebhook(PaymentTestSupport.Unsigned(body)).Should().BeFalse();
    }

    [Fact]
    public void Only_a_success_event_with_a_success_status_is_money_received()
    {
        var gateway = Gateway(new RecordingHttpHandler());

        var paid = gateway.ParseWebhook(Webhook("PAYMENT_SUCCESS_WEBHOOK", "SUCCESS"));
        paid.Kind.Should().Be(GatewayOutcomeKind.Paid);
        paid.ProviderOrderId.Should().Be("11111111-2222-3333-4444-555555555555");
        paid.ProviderPaymentId.Should().Be("5114933189368");
        paid.ReportedAmount.Should().Be(500.50m);

        gateway.ParseWebhook(Webhook("PAYMENT_USER_DROPPED_WEBHOOK", "USER_DROPPED")).Kind.Should().Be(GatewayOutcomeKind.Pending);
        gateway.ParseWebhook(Webhook("PAYMENT_CHARGES_WEBHOOK", "SUCCESS")).Kind.Should().Be(GatewayOutcomeKind.Pending, "a fee notice is not a payment");
        gateway.ParseWebhook(Webhook("PAYMENT_SUCCESS_WEBHOOK", "PENDING")).Kind.Should().Be(GatewayOutcomeKind.Pending, "the type and the status must agree");

        var failed = gateway.ParseWebhook(Webhook("PAYMENT_FAILED_WEBHOOK", "FAILED"));
        failed.Kind.Should().Be(GatewayOutcomeKind.Failed);
        failed.FailureReason.Should().Be("Bank declined.");
    }

    [Fact]
    public void The_reported_amount_is_the_order_amount_not_the_payment_amount_with_a_surcharge_on_top()
    {
        var gateway = Gateway(new RecordingHttpHandler());

        var paid = gateway.ParseWebhook(Webhook("PAYMENT_SUCCESS_WEBHOOK", "SUCCESS", orderAmount: 500m, paymentAmount: 511.80m));

        paid.ReportedAmount.Should().Be(500m, "the settlement tripwire compares orders, and a fee the payer bore is not a disagreement");
    }

    [Fact]
    public async Task Asked_about_an_order_it_reports_paid_when_any_attempt_succeeded()
    {
        var handler = new RecordingHttpHandler().Respond(HttpStatusCode.OK, """
            [{"cf_payment_id":"1","payment_status":"FAILED","payment_amount":500,"order_amount":500},
             {"cf_payment_id":"2","payment_status":"SUCCESS","payment_amount":500,"order_amount":500}]
            """);

        var outcome = await Gateway(handler).QueryOrderAsync("11111111-2222-3333-4444-555555555555");

        handler.Requests.Single().Uri.ToString().Should().Be("https://sandbox.cashfree.com/pg/orders/11111111-2222-3333-4444-555555555555/payments");
        outcome!.Kind.Should().Be(GatewayOutcomeKind.Paid);
        outcome.ProviderPaymentId.Should().Be("2");
        outcome.ReportedAmount.Should().Be(500m);
    }

    [Fact]
    public async Task Asked_about_an_order_with_no_successful_attempt_it_leaves_it_open()
    {
        var handler = new RecordingHttpHandler().Respond(HttpStatusCode.OK, """[{"cf_payment_id":"1","payment_status":"USER_DROPPED"}]""");

        (await Gateway(handler).QueryOrderAsync("x"))!.Kind.Should().Be(GatewayOutcomeKind.Pending);

        (await Gateway(new RecordingHttpHandler().Respond(HttpStatusCode.OK, "[]")).QueryOrderAsync("x"))!
            .Kind.Should().Be(GatewayOutcomeKind.Pending, "nothing attempted yet");
    }

    [Fact]
    public async Task The_checkout_page_fetches_the_session_and_opens_cashfree_in_sandbox_mode()
    {
        var handler = new RecordingHttpHandler().Respond(HttpStatusCode.OK,
            """{"order_id":"11111111-2222-3333-4444-555555555555","payment_session_id":"session_abc","order_status":"ACTIVE"}""");
        var order = new PaymentOrder { ProviderOrderId = "11111111-2222-3333-4444-555555555555", Amount = 500.50m, Currency = "INR" };
        var returnUrl = $"https://api.test/checkout/return/{order.Id}";

        var html = await Gateway(handler).RenderAsync(order, returnUrl);

        handler.Requests.Single().Uri.ToString().Should().Be("https://sandbox.cashfree.com/pg/orders/11111111-2222-3333-4444-555555555555");
        html.Should().Contain("https://sdk.cashfree.com/js/v3/cashfree.js");
        html.Should().Contain("\"paymentSessionId\":\"session_abc\"");
        html.Should().Contain("\"mode\":\"sandbox\"");
        html.Should().Contain("redirectTarget: '_self'");
        html.Should().Contain(returnUrl);
        html.Should().NotContain(ClientSecret, "the secret never leaves the server");
    }

    [Fact]
    public async Task The_checkout_page_in_live_mode_opens_production()
    {
        var handler = new RecordingHttpHandler().Respond(HttpStatusCode.OK, """{"payment_session_id":"session_live"}""");

        var html = await Gateway(handler, live: true).RenderAsync(new PaymentOrder { ProviderOrderId = "x" }, "https://api.test/checkout/return/x");

        html.Should().Contain("\"mode\":\"production\"");
    }
}
