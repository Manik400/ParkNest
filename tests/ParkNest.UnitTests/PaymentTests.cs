using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Application.Payments;
using ParkNest.Domain.Common;
using ParkNest.Domain.Payments;

namespace ParkNest.UnitTests;

/// <summary>
/// The webhook is the fast path that turns real money into credits, and it is anonymous by
/// necessity — the gateway has no bearer token. Its verification is therefore the single control
/// standing between the internet and free credits, so it gets tested hard.
///
/// The other paths — the return trip, the client's poll and the sweep, all asking the gateway —
/// race the webhook to report the same payment. What is defended below is that however they
/// arrive, an order credits exactly once and only for the amount recorded when it was created.
/// </summary>
public sealed class PaymentTests : IDisposable
{
    private const string Secret = "webhook-secret-for-tests";

    private readonly TestHarness _h = new();
    private readonly FakeGateway _gateway = new(Secret);

    private readonly PaymentOptions _paymentOptions = new()
    {
        OrderExpiryMinutes = 30,
        ReconcileAfterSeconds = 15,
        ReconcileMinIntervalSeconds = 10
    };

    private readonly IPaymentService _payments;
    private readonly IPaymentReconciler _reconciler;
    private readonly IPaymentOrderExpiry _expiry;

    public PaymentTests()
    {
        (_payments, _reconciler) = PaymentTestSupport.Create(_h, _gateway, _paymentOptions);

        _expiry = new PaymentOrderExpiry(
            _h.Db, _h.Clock,
            Microsoft.Extensions.Options.Options.Create(_paymentOptions),
            NullLogger<PaymentOrderExpiry>.Instance);
    }

    public void Dispose() => _h.Dispose();

    private static WebhookRequest Webhook(string body, string? signature = null) =>
        new(body, new Dictionary<string, string>
        {
            [FakeGateway.SignatureHeaderName] = signature ?? FakeGateway.Sign(Secret, body)
        });

    private static string CapturedBody(string providerOrderId, decimal? amount = null) =>
        amount is null
            ? $"{{\"event\":\"payment.captured\",\"order\":\"{providerOrderId}\",\"payment\":\"pay_1\"}}"
            : $"{{\"event\":\"payment.captured\",\"order\":\"{providerOrderId}\",\"payment\":\"pay_1\",\"amount\":{amount}}}";

    private static string FailedBody(string providerOrderId) =>
        $"{{\"event\":\"payment.failed\",\"order\":\"{providerOrderId}\",\"payment\":\"pay_0\"}}";

    private static GatewayOutcome Paid(string providerOrderId) =>
        new(providerOrderId, GatewayOutcomeKind.Paid, "pay_1", null, 500m);

    private async Task<int> RechargeCount() =>
        await _h.Db.LedgerTransactions.CountAsync(t => t.Type == LedgerTransactionType.Recharge);

    private async Task<decimal> Balance(Guid userId) =>
        (await _h.Wallets.GetOrCreateWalletAsync(userId)).SpendableBalance;

    // --- Starting a payment ------------------------------------------------------

    [Fact]
    public async Task Starting_a_payment_issues_no_credits()
    {
        var user = await _h.AddUserAsync(UserRole.Both);

        var result = await _payments.StartAsync(500m);

        result.Amount.Should().Be(500m);
        (await Balance(user.Id)).Should().Be(0m);

        var order = await _h.Db.PaymentOrders.SingleAsync();
        order.Status.Should().Be(PaymentOrderStatus.Created);
    }

    [Fact]
    public async Task The_gateway_is_given_our_return_url_the_payer_and_a_matching_expiry()
    {
        var user = await _h.AddUserAsync(UserRole.Both);

        var started = await _payments.StartAsync(500m);

        var request = _gateway.LastRequest!;
        request.OrderId.Should().Be(started.OrderId);
        request.ReturnUrl.Should().Be($"/checkout/return/{started.OrderId}");
        request.Customer.UserId.Should().Be(user.Id);
        request.Customer.Phone.Should().Be(user.Phone);
        request.ExpiresAt.Should().Be(_h.Clock.UtcNow.AddMinutes(_paymentOptions.OrderExpiryMinutes));
    }

    [Fact]
    public async Task The_client_that_started_the_payment_is_recorded_on_the_order()
    {
        await _h.AddUserAsync(UserRole.Both);

        var started = await _payments.StartAsync(500m, "App");

        (await _h.Db.PaymentOrders.SingleAsync(o => o.Id == started.OrderId)).ReturnTo.Should().Be("app");
    }

    [Fact]
    public async Task An_unknown_return_target_is_refused()
    {
        await _h.AddUserAsync(UserRole.Both);

        var act = () => _payments.StartAsync(500m, "https://evil.example");

        await act.Should().ThrowAsync<DomainException>();
    }

    [Theory]
    [InlineData(50)]
    [InlineData(50_000)]
    public async Task Amounts_outside_the_allowed_range_are_refused(decimal amount)
    {
        await _h.AddUserAsync(UserRole.Both);

        var act = () => _payments.StartAsync(amount);

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task An_unauthenticated_caller_cannot_start_a_payment()
    {
        _h.CurrentUser.SignOut();

        var act = () => _payments.StartAsync(500m);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    // --- Webhook verification -----------------------------------------------------

    [Fact]
    public async Task A_webhook_with_a_bad_signature_credits_nothing()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        var act = () => _payments.HandleWebhookAsync(Webhook(CapturedBody(started.ProviderOrderId), "deadbeef"));

        await act.Should().ThrowAsync<UnauthorizedException>();
        (await Balance(user.Id)).Should().Be(0m);
    }

    [Fact]
    public async Task A_webhook_with_no_signature_credits_nothing()
    {
        await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        var act = () => _payments.HandleWebhookAsync(PaymentTestSupport.Unsigned(CapturedBody(started.ProviderOrderId)));

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task A_tampered_body_fails_verification()
    {
        await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        var body = CapturedBody(started.ProviderOrderId);
        var signature = FakeGateway.Sign(Secret, body);

        // Same signature, one character of the body changed.
        var act = () => _payments.HandleWebhookAsync(Webhook(body.Replace("pay_1", "pay_2"), signature));

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task The_signature_header_is_found_whatever_its_case()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);
        var body = CapturedBody(started.ProviderOrderId);

        // HTTP header names are case-insensitive, and proxies do rewrite them.
        var request = new WebhookRequest(body, new Dictionary<string, string>
        {
            [FakeGateway.SignatureHeaderName.ToLowerInvariant()] = FakeGateway.Sign(Secret, body)
        });

        await _payments.HandleWebhookAsync(request);

        (await Balance(user.Id)).Should().Be(500m);
    }

    // --- Applying a verified outcome ----------------------------------------------

    [Fact]
    public async Task A_verified_capture_credits_the_amount_recorded_at_order_time()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        var result = await _payments.HandleWebhookAsync(Webhook(CapturedBody(started.ProviderOrderId)));

        result.Accepted.Should().BeTrue();
        (await Balance(user.Id)).Should().Be(500m);
    }

    [Fact]
    public async Task A_reported_amount_that_disagrees_with_the_order_credits_nothing()
    {
        // Signed, and claiming a wildly different figure. The credit would come from our own
        // record anyway, so this cannot inflate anything — but it means our books and the
        // gateway's disagree, and nothing is issued until a human has looked.
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        var result = await _payments.HandleWebhookAsync(Webhook(CapturedBody(started.ProviderOrderId, 999_999m)));

        result.Message.Should().Contain("mismatch");
        (await Balance(user.Id)).Should().Be(0m);
        (await _h.Db.PaymentOrders.SingleAsync()).Status.Should().Be(PaymentOrderStatus.Created);
    }

    [Fact]
    public async Task A_replayed_webhook_credits_only_once()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);
        var webhook = Webhook(CapturedBody(started.ProviderOrderId));

        // Gateways retry until acknowledged, so duplicates are routine rather than exceptional.
        await _payments.HandleWebhookAsync(webhook);
        await _payments.HandleWebhookAsync(webhook);
        await _payments.HandleWebhookAsync(webhook);

        (await Balance(user.Id)).Should().Be(500m);
        (await RechargeCount()).Should().Be(1);
    }

    [Fact]
    public async Task A_failed_payment_is_recorded_and_credits_nothing()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        var result = await _payments.HandleWebhookAsync(Webhook(FailedBody(started.ProviderOrderId)));

        result.Accepted.Should().BeTrue();
        (await Balance(user.Id)).Should().Be(0m);
        (await _h.Db.PaymentOrders.SingleAsync()).Status.Should().Be(PaymentOrderStatus.Failed);
    }

    [Fact]
    public async Task A_failed_attempt_followed_by_a_successful_retry_credits_once()
    {
        // Checkout sheets let the user try another UPI app after a failure, on the same order.
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        await _payments.HandleWebhookAsync(Webhook(FailedBody(started.ProviderOrderId)));
        await _payments.HandleWebhookAsync(Webhook(CapturedBody(started.ProviderOrderId)));

        (await Balance(user.Id)).Should().Be(500m);
        var order = await _h.Db.PaymentOrders.SingleAsync();
        order.Status.Should().Be(PaymentOrderStatus.Paid);
        order.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task A_signed_webhook_for_an_unknown_order_is_acknowledged_but_credits_nothing()
    {
        await _h.AddUserAsync(UserRole.Both);

        var result = await _payments.HandleWebhookAsync(Webhook(CapturedBody("order_that_does_not_exist")));

        // Acknowledged so the gateway stops retrying; nothing credited because nothing matches.
        result.Accepted.Should().BeFalse();
        (await _h.Db.LedgerTransactions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_authorised_but_uncaptured_payment_changes_nothing()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        // "authorized" means the funds are merely held and can still fall through. It is not a
        // failure either — the capture that follows must still be able to credit.
        var body = $"{{\"event\":\"payment.authorized\",\"order\":\"{started.ProviderOrderId}\",\"payment\":\"pay_1\"}}";
        await _payments.HandleWebhookAsync(Webhook(body));

        (await Balance(user.Id)).Should().Be(0m);
        (await _h.Db.PaymentOrders.SingleAsync()).Status.Should().Be(PaymentOrderStatus.Created);
    }

    // --- A webhook that proves only who sent it -----------------------------------

    [Fact]
    public async Task A_non_authoritative_webhook_is_confirmed_with_the_gateway_before_crediting()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        var gateway = new FakeGateway(Secret, webhookIsAuthoritative: false)
        {
            Status = id => new GatewayOutcome(id, GatewayOutcomeKind.Pending)
        };
        var (payments, _) = PaymentTestSupport.Create(_h, gateway, _paymentOptions);
        var started = await payments.StartAsync(500m);

        // Verified, and it says paid — but its credential does not cover the body, and the gateway
        // itself says the payment is still pending. The gateway's answer wins.
        await payments.HandleWebhookAsync(Webhook(CapturedBody(started.ProviderOrderId)));

        (await Balance(user.Id)).Should().Be(0m);

        gateway.Status = Paid;
        await payments.HandleWebhookAsync(Webhook(CapturedBody(started.ProviderOrderId)));

        (await Balance(user.Id)).Should().Be(500m);
    }

    // --- Asking the gateway -------------------------------------------------------

    [Fact]
    public async Task A_payment_whose_webhook_never_arrives_is_credited_by_asking()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);
        _gateway.Status = Paid;

        (await _reconciler.ReconcileAsync(started.OrderId, "return")).Should().BeTrue();

        (await Balance(user.Id)).Should().Be(500m);
        (await _h.Db.PaymentOrders.SingleAsync()).Status.Should().Be(PaymentOrderStatus.Paid);
    }

    [Fact]
    public async Task A_webhook_and_a_status_check_reporting_the_same_payment_credit_once()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);
        _gateway.Status = Paid;

        await _reconciler.ReconcileAsync(started.OrderId, "return");
        var result = await _payments.HandleWebhookAsync(Webhook(CapturedBody(started.ProviderOrderId)));
        await _reconciler.ReconcileAsync(started.OrderId, "poll");

        result.Message.Should().Be("Already processed.");
        (await Balance(user.Id)).Should().Be(500m);
        (await RechargeCount()).Should().Be(1);
    }

    [Fact]
    public async Task A_failed_order_is_rescued_when_the_gateway_reports_the_retry_succeeded()
    {
        // In development there is no webhook for the retry; the return trip is what notices.
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);
        await _payments.HandleWebhookAsync(Webhook(FailedBody(started.ProviderOrderId)));

        _gateway.Status = Paid;
        await _reconciler.ReconcileAsync(started.OrderId, "return");

        (await Balance(user.Id)).Should().Be(500m);
    }

    [Fact]
    public async Task Asking_when_the_gateway_cannot_answer_changes_nothing()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        (await _reconciler.ReconcileAsync(started.OrderId, "return")).Should().BeFalse();

        (await Balance(user.Id)).Should().Be(0m);
        var order = await _h.Db.PaymentOrders.SingleAsync();
        order.Status.Should().Be(PaymentOrderStatus.Created);
        order.LastCheckedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_gateway_error_while_asking_is_swallowed_so_a_poll_never_fails_for_it()
    {
        await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);
        _gateway.Status = _ => throw new HttpRequestException("provider down");

        var act = () => _reconciler.ReconcileAsync(started.OrderId, "poll");

        (await act.Should().NotThrowAsync()).Which.Should().BeFalse();
    }

    [Fact]
    public async Task Polling_asks_the_gateway_only_once_the_order_is_old_enough_and_not_on_every_poll()
    {
        await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);
        _gateway.Status = id => new GatewayOutcome(id, GatewayOutcomeKind.Pending);

        await _payments.GetOrderAsync(started.OrderId);
        _gateway.StatusQueries.Should().Be(0, "a brand-new order is someone still at the checkout sheet");

        _h.Clock.Advance(TimeSpan.FromSeconds(_paymentOptions.ReconcileAfterSeconds + 1));
        await _payments.GetOrderAsync(started.OrderId);
        await _payments.GetOrderAsync(started.OrderId);
        _gateway.StatusQueries.Should().Be(1, "a client polling every couple of seconds must not become a call to the provider each time");

        _h.Clock.Advance(TimeSpan.FromSeconds(_paymentOptions.ReconcileMinIntervalSeconds));
        await _payments.GetOrderAsync(started.OrderId);
        _gateway.StatusQueries.Should().Be(2);
    }

    [Fact]
    public async Task Polling_reports_Paid_once_the_gateway_says_so_even_with_no_webhook()
    {
        await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);
        _gateway.Status = Paid;

        _h.Clock.Advance(TimeSpan.FromSeconds(_paymentOptions.ReconcileAfterSeconds + 1));

        (await _payments.GetOrderAsync(started.OrderId)).Status.Should().Be("Paid");
    }

    [Fact]
    public async Task The_sweep_credits_a_paid_order_before_it_would_have_expired_it()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);
        _gateway.Status = Paid;

        _h.Clock.Advance(TimeSpan.FromMinutes(_paymentOptions.OrderExpiryMinutes + 1));
        (await _reconciler.ReconcilePendingAsync()).Should().Be(1);
        (await _expiry.ExpireStaleOrdersAsync()).Should().Be(0);

        (await Balance(user.Id)).Should().Be(500m);
        (await _h.Db.PaymentOrders.SingleAsync(o => o.Id == started.OrderId)).Status.Should().Be(PaymentOrderStatus.Paid);
    }

    [Fact]
    public async Task The_sweep_leaves_orders_younger_than_a_minute_alone()
    {
        await _h.AddUserAsync(UserRole.Both);
        await _payments.StartAsync(500m);
        _gateway.Status = Paid;

        await _reconciler.ReconcilePendingAsync();

        _gateway.StatusQueries.Should().Be(0);
    }

    // --- Expiry -------------------------------------------------------------------

    [Fact]
    public async Task An_order_left_unpaid_past_the_window_is_cancelled()
    {
        await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        _h.Clock.Advance(TimeSpan.FromMinutes(_paymentOptions.OrderExpiryMinutes + 1));
        var expired = await _expiry.ExpireStaleOrdersAsync();

        expired.Should().Be(1);
        var order = await _h.Db.PaymentOrders.SingleAsync(o => o.Id == started.OrderId);
        order.Status.Should().Be(PaymentOrderStatus.Cancelled);
        order.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task An_order_still_inside_the_window_is_left_alone()
    {
        await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        _h.Clock.Advance(TimeSpan.FromMinutes(_paymentOptions.OrderExpiryMinutes - 1));
        var expired = await _expiry.ExpireStaleOrdersAsync();

        expired.Should().Be(0);
        var order = await _h.Db.PaymentOrders.SingleAsync(o => o.Id == started.OrderId);
        order.Status.Should().Be(PaymentOrderStatus.Created);
    }

    [Fact]
    public async Task Expiry_never_touches_an_order_that_was_already_paid()
    {
        await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);
        await _payments.HandleWebhookAsync(Webhook(CapturedBody(started.ProviderOrderId)));

        _h.Clock.Advance(TimeSpan.FromDays(1));
        await _expiry.ExpireStaleOrdersAsync();

        var order = await _h.Db.PaymentOrders.SingleAsync(o => o.Id == started.OrderId);
        order.Status.Should().Be(PaymentOrderStatus.Paid);
    }

    [Fact]
    public async Task A_payment_that_lands_after_expiry_still_credits()
    {
        // Expiry is our bookkeeping, not the gateway's. If the money moved, the credits are owed
        // whatever we had already written down.
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        _h.Clock.Advance(TimeSpan.FromMinutes(_paymentOptions.OrderExpiryMinutes + 1));
        await _expiry.ExpireStaleOrdersAsync();

        var result = await _payments.HandleWebhookAsync(Webhook(CapturedBody(started.ProviderOrderId)));

        result.Accepted.Should().BeTrue();
        (await Balance(user.Id)).Should().Be(500m);

        var order = await _h.Db.PaymentOrders.SingleAsync(o => o.Id == started.OrderId);
        order.Status.Should().Be(PaymentOrderStatus.Paid);
        order.FailureReason.Should().BeNull("a paid order must not carry the reason it expired");
    }

    [Fact]
    public async Task Sweeping_twice_cancels_an_order_once()
    {
        await _h.AddUserAsync(UserRole.Both);
        await _payments.StartAsync(500m);

        _h.Clock.Advance(TimeSpan.FromMinutes(_paymentOptions.OrderExpiryMinutes + 1));
        await _expiry.ExpireStaleOrdersAsync();
        var second = await _expiry.ExpireStaleOrdersAsync();

        second.Should().Be(0, "a second instance running the same sweep must find nothing to do");
    }
}
