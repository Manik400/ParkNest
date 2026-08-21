using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ParkNest.Application.Options;
using ParkNest.Application.Payments;
using ParkNest.Domain.Common;
using ParkNest.Domain.Payments;
using ParkNest.Infrastructure.Payments;

namespace ParkNest.UnitTests;

/// <summary>
/// The sandbox gateway signs the callbacks it later verifies, which is exactly the property that
/// makes it useful in development and disqualifying in Production. These tests pin down that it
/// reproduces the real protocol rather than waving payments through: a body it did not sign must
/// fail, and an authorised-but-uncaptured payment must not credit.
/// </summary>
public sealed class SandboxPaymentTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly SandboxPaymentGateway _gateway;
    private readonly IPaymentService _payments;

    public SandboxPaymentTests()
    {
        _gateway = NewGateway();
        _payments = new PaymentService(
            _h.Db, _gateway, _h.Wallets, _h.CurrentUser, _h.Clock,
            Options.Create(_h.Options),
            NullLogger<PaymentService>.Instance);
    }

    public void Dispose() => _h.Dispose();

    /// <summary>No configured secret, so each instance generates its own per-process signing key.</summary>
    private static SandboxPaymentGateway NewGateway(string? secret = null) =>
        new(Options.Create(new PaymentOptions { Provider = "Sandbox", WebhookSecret = secret ?? string.Empty }));

    // --- The gateway on its own ------------------------------------------------

    [Fact]
    public async Task An_order_carries_a_checkout_url_pointing_at_its_own_id()
    {
        var order = await _gateway.CreateOrderAsync(Guid.NewGuid(), 500m, "INR");

        order.ProviderOrderId.Should().StartWith("sbx_order_");

        using var payload = JsonDocument.Parse(order.CheckoutPayload);
        payload.RootElement.GetProperty("checkout_url").GetString()
            .Should().Be($"/sandbox/checkout/{order.ProviderOrderId}");
    }

    [Fact]
    public void A_webhook_it_built_verifies_against_itself()
    {
        var webhook = _gateway.BuildWebhook("sbx_order_1", succeeded: true);

        _gateway.VerifyWebhookSignature(webhook.Body, webhook.Signature).Should().BeTrue();
    }

    [Fact]
    public void A_tampered_body_fails_verification()
    {
        var webhook = _gateway.BuildWebhook("sbx_order_1", succeeded: true);
        var tampered = webhook.Body.Replace("sbx_order_1", "sbx_order_2");

        _gateway.VerifyWebhookSignature(tampered, webhook.Signature).Should().BeFalse();
    }

    [Fact]
    public void A_signature_from_a_different_instance_is_rejected()
    {
        // Two processes, two random keys. This is what stops an abandoned checkout page from
        // staying replayable across a restart.
        var other = NewGateway();
        var webhook = other.BuildWebhook("sbx_order_1", succeeded: true);

        _gateway.VerifyWebhookSignature(webhook.Body, webhook.Signature).Should().BeFalse();
    }

    [Fact]
    public void A_configured_secret_makes_signatures_stable_across_instances()
    {
        var first = NewGateway("shared-sandbox-secret");
        var second = NewGateway("shared-sandbox-secret");

        var webhook = first.BuildWebhook("sbx_order_1", succeeded: true);

        second.VerifyWebhookSignature(webhook.Body, webhook.Signature).Should().BeTrue();
    }

    [Fact]
    public void An_empty_signature_is_rejected()
    {
        var webhook = _gateway.BuildWebhook("sbx_order_1", succeeded: true);

        _gateway.VerifyWebhookSignature(webhook.Body, "").Should().BeFalse();
    }

    [Fact]
    public void A_capture_parses_as_success_and_a_decline_does_not()
    {
        var captured = _gateway.BuildWebhook("sbx_order_1", succeeded: true);
        var declined = _gateway.BuildWebhook("sbx_order_1", succeeded: false, failureReason: "Card declined.");

        var success = _gateway.ParseWebhook(captured.Body);
        success.Succeeded.Should().BeTrue();
        success.ProviderOrderId.Should().Be("sbx_order_1");
        success.ProviderPaymentId.Should().StartWith("sbx_pay_");

        var failure = _gateway.ParseWebhook(declined.Body);
        failure.Succeeded.Should().BeFalse();
        failure.FailureReason.Should().Be("Card declined.");
    }

    [Fact]
    public void An_authorised_but_uncaptured_payment_is_not_a_success()
    {
        // Hand-built, because the sandbox's own button only produces captures and declines. Funds
        // that are merely authorised can still fall through, so they must not read as money in.
        const string body = """
            {"event":"payment.authorized","payload":{"payment":{"entity":{"id":"sbx_pay_1","order_id":"sbx_order_1"}}}}
            """;

        _gateway.ParseWebhook(body).Succeeded.Should().BeFalse();
    }

    // --- Through the payment service ------------------------------------------

    [Fact]
    public async Task A_full_sandbox_recharge_credits_the_wallet_once()
    {
        var user = await _h.AddUserAsync(UserRole.Both);

        var started = await _payments.StartAsync(500m);
        var webhook = _gateway.BuildWebhook(started.ProviderOrderId, succeeded: true);

        // Delivered twice, as a real gateway retrying an unacknowledged callback would.
        await _payments.HandleWebhookAsync(webhook.Body, webhook.Signature);
        await _payments.HandleWebhookAsync(webhook.Body, webhook.Signature);

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(user.Id);
        wallet.SpendableBalance.Should().Be(500m);

        (await _h.Db.LedgerTransactions.CountAsync(t => t.Type == LedgerTransactionType.Recharge))
            .Should().Be(1);

        var order = await _h.Db.PaymentOrders.SingleAsync();
        order.Status.Should().Be(PaymentOrderStatus.Paid);
        order.LedgerTransactionId.Should().NotBeNull();
    }

    [Fact]
    public async Task A_declined_sandbox_payment_credits_nothing()
    {
        var user = await _h.AddUserAsync(UserRole.Both);

        var started = await _payments.StartAsync(500m);
        var webhook = _gateway.BuildWebhook(started.ProviderOrderId, succeeded: false, failureReason: "Declined.");

        await _payments.HandleWebhookAsync(webhook.Body, webhook.Signature);

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(user.Id);
        wallet.SpendableBalance.Should().Be(0m);

        var order = await _h.Db.PaymentOrders.SingleAsync();
        order.Status.Should().Be(PaymentOrderStatus.Failed);
        order.FailureReason.Should().Be("Declined.");
    }

    // --- Order status ----------------------------------------------------------

    [Fact]
    public async Task An_order_reports_Created_until_the_callback_lands_and_Paid_after()
    {
        await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        // This is the state the client polls on while the user is still at the checkout sheet.
        var before = await _payments.GetOrderAsync(started.OrderId);
        before.Status.Should().Be("Created");
        before.CompletedAt.Should().BeNull();
        before.Amount.Should().Be(500m);

        var webhook = _gateway.BuildWebhook(started.ProviderOrderId, succeeded: true);
        await _payments.HandleWebhookAsync(webhook.Body, webhook.Signature);

        var after = await _payments.GetOrderAsync(started.OrderId);
        after.Status.Should().Be("Paid");
        after.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Another_user_cannot_read_someone_elses_order()
    {
        await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        // Order ids are guessable enough in aggregate that leaving this open would expose one
        // user's recharge history and amounts to another.
        await _h.AddUserAsync(UserRole.Both);

        var act = () => _payments.GetOrderAsync(started.OrderId);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task An_admin_can_read_any_order()
    {
        await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        _h.CurrentUser.SignIn(Guid.NewGuid(), UserRole.Admin);

        (await _payments.GetOrderAsync(started.OrderId)).OrderId.Should().Be(started.OrderId);
    }

    [Fact]
    public async Task An_order_that_does_not_exist_is_a_domain_error()
    {
        await _h.AddUserAsync(UserRole.Both);

        var act = () => _payments.GetOrderAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<DomainException>();
    }
}
