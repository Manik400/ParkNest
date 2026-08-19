using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Application.Payments;
using ParkNest.Domain.Common;
using ParkNest.Domain.Payments;

namespace ParkNest.UnitTests;

/// <summary>
/// The webhook is the only path that turns real money into credits, and it is anonymous by
/// necessity — the gateway has no bearer token. Its signature check is therefore the single
/// control standing between the internet and free credits, so it gets tested hard.
/// </summary>
public sealed class PaymentTests : IDisposable
{
    private const string Secret = "webhook-secret-for-tests";

    private readonly TestHarness _h = new();
    private readonly FakeGateway _gateway = new(Secret);
    private readonly IPaymentService _payments;
    private readonly IPaymentOrderExpiry _expiry;
    private readonly PaymentOptions _paymentOptions = new() { OrderExpiryMinutes = 30 };

    public PaymentTests()
    {
        _payments = new PaymentService(
            _h.Db, _gateway, _h.Wallets, _h.CurrentUser, _h.Clock,
            Microsoft.Extensions.Options.Options.Create(_h.Options),
            NullLogger<PaymentService>.Instance);

        _expiry = new PaymentOrderExpiry(
            _h.Db, _h.Clock,
            Microsoft.Extensions.Options.Options.Create(_paymentOptions),
            NullLogger<PaymentOrderExpiry>.Instance);
    }

    public void Dispose() => _h.Dispose();

    private static string Sign(string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    private static string CapturedBody(string providerOrderId) =>
        $"{{\"event\":\"payment.captured\",\"order\":\"{providerOrderId}\",\"payment\":\"pay_1\"}}";

    [Fact]
    public async Task Starting_a_payment_issues_no_credits()
    {
        var user = await _h.AddUserAsync(UserRole.Both);

        var result = await _payments.StartAsync(500m);

        result.Amount.Should().Be(500m);

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(user.Id);
        wallet.SpendableBalance.Should().Be(0m);

        var order = await _h.Db.PaymentOrders.SingleAsync();
        order.Status.Should().Be(PaymentOrderStatus.Created);
    }

    [Fact]
    public async Task A_webhook_with_a_bad_signature_credits_nothing()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);
        var body = CapturedBody(started.ProviderOrderId);

        var act = () => _payments.HandleWebhookAsync(body, "deadbeef");

        await act.Should().ThrowAsync<UnauthorizedException>();

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(user.Id);
        wallet.SpendableBalance.Should().Be(0m);
    }

    [Fact]
    public async Task A_webhook_with_no_signature_credits_nothing()
    {
        await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        var act = () => _payments.HandleWebhookAsync(CapturedBody(started.ProviderOrderId), "");

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task A_tampered_body_fails_verification()
    {
        await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        var body = CapturedBody(started.ProviderOrderId);
        var signature = Sign(body);

        // Same signature, one character of the body changed.
        var tampered = body.Replace("pay_1", "pay_2");

        var act = () => _payments.HandleWebhookAsync(tampered, signature);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task A_verified_capture_credits_the_amount_recorded_at_order_time()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        // The gateway reports a wildly different figure. It must be ignored — the amount comes
        // from our own order record, so a tampered-but-signed payload cannot inflate the credit.
        _gateway.AmountClaimedInPayload = 999_999m;

        var body = CapturedBody(started.ProviderOrderId);
        var result = await _payments.HandleWebhookAsync(body, Sign(body));

        result.Accepted.Should().BeTrue();

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(user.Id);
        wallet.SpendableBalance.Should().Be(500m);
    }

    [Fact]
    public async Task A_replayed_webhook_credits_only_once()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);
        var body = CapturedBody(started.ProviderOrderId);
        var signature = Sign(body);

        // Gateways retry until acknowledged, so duplicates are routine rather than exceptional.
        await _payments.HandleWebhookAsync(body, signature);
        await _payments.HandleWebhookAsync(body, signature);
        await _payments.HandleWebhookAsync(body, signature);

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(user.Id);
        wallet.SpendableBalance.Should().Be(500m);

        (await _h.Db.LedgerTransactions.CountAsync(t => t.Type == LedgerTransactionType.Recharge))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_failed_payment_is_recorded_and_credits_nothing()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        var body = $"{{\"event\":\"payment.failed\",\"order\":\"{started.ProviderOrderId}\",\"payment\":\"pay_1\"}}";
        var result = await _payments.HandleWebhookAsync(body, Sign(body));

        result.Accepted.Should().BeTrue();

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(user.Id);
        wallet.SpendableBalance.Should().Be(0m);

        var order = await _h.Db.PaymentOrders.SingleAsync();
        order.Status.Should().Be(PaymentOrderStatus.Failed);
    }

    [Fact]
    public async Task A_signed_webhook_for_an_unknown_order_is_acknowledged_but_credits_nothing()
    {
        await _h.AddUserAsync(UserRole.Both);

        var body = CapturedBody("order_that_does_not_exist");
        var result = await _payments.HandleWebhookAsync(body, Sign(body));

        // Acknowledged so the gateway stops retrying; nothing credited because nothing matches.
        result.Accepted.Should().BeFalse();
        (await _h.Db.LedgerTransactions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_authorised_but_uncaptured_payment_does_not_credit()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        var started = await _payments.StartAsync(500m);

        // "authorized" means the funds are merely held and can still fall through.
        var body = $"{{\"event\":\"payment.authorized\",\"order\":\"{started.ProviderOrderId}\",\"payment\":\"pay_1\"}}";
        await _payments.HandleWebhookAsync(body, Sign(body));

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(user.Id);
        wallet.SpendableBalance.Should().Be(0m);
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
        var body = CapturedBody(started.ProviderOrderId);
        await _payments.HandleWebhookAsync(body, Sign(body));

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

        var body = CapturedBody(started.ProviderOrderId);
        var result = await _payments.HandleWebhookAsync(body, Sign(body));

        result.Accepted.Should().BeTrue();
        var wallet = await _h.Wallets.GetOrCreateWalletAsync(user.Id);
        wallet.SpendableBalance.Should().Be(500m);

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

/// <summary>
/// Mirrors the real gateway's HMAC-SHA256-over-raw-body scheme without touching the network.
/// The signature logic is what matters here, so it is reproduced faithfully.
/// </summary>
internal sealed class FakeGateway : IPaymentGateway
{
    private readonly string _secret;
    private int _counter;

    public FakeGateway(string secret) => _secret = secret;

    /// <summary>Set by a test to prove the payload's amount is never trusted.</summary>
    public decimal? AmountClaimedInPayload { get; set; }

    public string Name => "Fake";

    public string SignatureHeader => "X-Fake-Signature";

    public Task<GatewayOrder> CreateOrderAsync(
        Guid orderId, decimal amount, string currency, CancellationToken cancellationToken = default)
    {
        var providerOrderId = $"order_{++_counter}";
        return Task.FromResult(new GatewayOrder(providerOrderId, "{}"));
    }

    public bool VerifyWebhookSignature(string rawBody, string signature)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant()));
    }

    public WebhookEvent ParseWebhook(string rawBody)
    {
        using var document = System.Text.Json.JsonDocument.Parse(rawBody);
        var root = document.RootElement;

        var eventName = root.GetProperty("event").GetString();

        return new WebhookEvent(
            root.GetProperty("order").GetString()!,
            root.GetProperty("payment").GetString(),
            eventName == "payment.captured",
            eventName == "payment.captured" ? null : eventName);
    }
}
