using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Wallets;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

public sealed class LedgerServiceTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task PostAsync_rejects_a_transaction_whose_legs_do_not_balance()
    {
        var user = await _h.AddUserAsync(UserRole.Renter);
        var wallet = await _h.Wallets.GetOrCreateWalletAsync(user.Id);

        var act = () => _h.Ledger.PostAsync(
            LedgerTransactionType.AdminAdjustment,
            "unbalanced-1",
            new[]
            {
                LedgerPosting.Debit(null, LedgerAccountType.ExternalFunding, 100m),
                LedgerPosting.Credit(wallet.Id, LedgerAccountType.Spendable, 90m)
            });

        await act.Should().ThrowAsync<UnbalancedLedgerTransactionException>();
        (await _h.Db.LedgerTransactions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PostAsync_replays_the_same_transaction_for_a_repeated_idempotency_key()
    {
        var user = await _h.AddUserAsync(UserRole.Renter);

        await _h.Wallets.RechargeAsync(user.Id, 500m, "recharge-abc");
        await _h.Wallets.RechargeAsync(user.Id, 500m, "recharge-abc");

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(user.Id);

        // The retry must not credit a second 500.
        wallet.SpendableBalance.Should().Be(500m);
        (await _h.Db.LedgerTransactions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Wallet_balances_always_replay_from_the_entry_stream()
    {
        var renter = await _h.AddUserAsync(UserRole.Renter);
        var host = await _h.AddUserAsync(UserRole.Host);

        await _h.Wallets.RechargeAsync(renter.Id, 1000m, "r1");
        var wallet = await _h.Wallets.GetOrCreateWalletAsync(renter.Id);

        var bookingId = Guid.NewGuid();
        await _h.Wallets.PlaceHoldAsync(renter.Id, bookingId, 300m, "h1");
        await _h.Wallets.ReleaseHoldAsync(renter.Id, bookingId, 100m, "rel1");
        await _h.Wallets.SettleAsync(new SettlementRequest(bookingId, renter.Id, host.Id, 200m, "s1"));

        var replayed = await _h.Ledger.RecomputeFromEntriesAsync(wallet.Id);

        replayed.Spendable.Should().Be(wallet.SpendableBalance);
        replayed.Held.Should().Be(wallet.HeldBalance);
        replayed.Earning.Should().Be(wallet.EarningBalance);
    }

    [Fact]
    public async Task Every_persisted_transaction_is_balanced()
    {
        var renter = await _h.AddUserAsync(UserRole.Renter);
        var host = await _h.AddUserAsync(UserRole.Host);

        await _h.Wallets.RechargeAsync(renter.Id, 1000m, "r1");
        var bookingId = Guid.NewGuid();
        await _h.Wallets.PlaceHoldAsync(renter.Id, bookingId, 250m, "h1");
        await _h.Wallets.SettleAsync(new SettlementRequest(bookingId, renter.Id, host.Id, 250m, "s1"));

        var transactions = await _h.Db.LedgerTransactions.Include(t => t.Entries).ToListAsync();

        transactions.Should().NotBeEmpty();
        transactions.Should().OnlyContain(t => t.IsBalanced);
    }

    [Fact]
    public async Task A_hold_cannot_exceed_the_spendable_balance()
    {
        var renter = await _h.AddUserAsync(UserRole.Renter);
        await _h.Wallets.RechargeAsync(renter.Id, 100m, "r1");

        var act = () => _h.Wallets.PlaceHoldAsync(renter.Id, Guid.NewGuid(), 150m, "h1");

        (await act.Should().ThrowAsync<InsufficientCreditsException>())
            .Which.Available.Should().Be(100m);
    }

    [Fact]
    public async Task Settlement_splits_the_hold_between_host_and_platform_commission()
    {
        var renter = await _h.AddUserAsync(UserRole.Renter);
        var host = await _h.AddUserAsync(UserRole.Host);

        await _h.Wallets.RechargeAsync(renter.Id, 1000m, "r1");
        var bookingId = Guid.NewGuid();
        await _h.Wallets.PlaceHoldAsync(renter.Id, bookingId, 200m, "h1");

        var result = await _h.Wallets.SettleAsync(
            new SettlementRequest(bookingId, renter.Id, host.Id, 200m, "s1"));

        // 10% commission in the test harness.
        result.PlatformFee.Should().Be(20m);
        result.HostCredited.Should().Be(180m);

        var hostWallet = await _h.Wallets.GetOrCreateWalletAsync(host.Id);
        hostWallet.EarningBalance.Should().Be(180m);

        var renterWallet = await _h.Wallets.GetOrCreateWalletAsync(renter.Id);
        renterWallet.HeldBalance.Should().Be(0m);
        renterWallet.SpendableBalance.Should().Be(800m);
    }
}
