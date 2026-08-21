using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Listings;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

public sealed class PricingAndPayoutTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Theory]
    [InlineData(1, 15)]
    [InlineData(15, 15)]
    [InlineData(16, 30)]
    [InlineData(45, 45)]
    [InlineData(61, 75)]
    public void Durations_round_up_to_the_billing_increment(int actual, int expected) =>
        _h.PricingService.RoundUpToIncrement(actual).Should().Be(expected);

    [Fact]
    public async Task A_listing_priced_above_the_city_band_cannot_go_live()
    {
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync(min: 20m, max: 120m);

        // Availability is present so publishing fails on the price alone, which is what this covers.
        var draft = await _h.Listings.CreateDraftAsync(new CreateListingRequest(
            "Driveway", "12 Main Rd", "Bengaluru", null, 12.97, 77.59, 500m,
            new[] { VehicleType.FourWheeler },
            new[] { new AvailabilityWindowRequest(DayOfWeek.Tuesday, new TimeOnly(9, 0), new TimeOnly(17, 0)) }));

        var act = () => _h.Listings.PublishAsync(draft.Id);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*outside the Bengaluru band*");
    }

    [Fact]
    public async Task A_zone_specific_band_overrides_the_city_wide_one()
    {
        await _h.AddBandAsync(min: 20m, max: 60m);

        _h.Db.CityPricingConfigs.Add(new ParkNest.Domain.Pricing.CityPricingConfig
        {
            City = "Bengaluru",
            Zone = "cbd",
            VehicleType = VehicleType.FourWheeler,
            MinPricePerHour = 80m,
            MaxPricePerHour = 200m,
            OverstayMultiplier = 1.25m
        });
        await _h.Db.SaveChangesAsync();

        var band = await _h.PricingService.GetBandAsync("Bengaluru", "cbd", VehicleType.FourWheeler);

        band.MaxPricePerHour.Should().Be(200m);
        band.OverstayMultiplier.Should().Be(1.25m);
    }

    [Fact]
    public async Task Cash_out_is_blocked_below_the_minimum_threshold()
    {
        var host = await _h.AddUserAsync(UserRole.Host, KycStatus.Verified);
        await CreditHostAsync(host.Id, 600m);

        var act = () => _h.Wallets.RequestCashOutAsync(host.Id, 100m, "co-1");

        await act.Should().ThrowAsync<DomainException>().WithMessage("*Minimum cash-out*");
    }

    [Fact]
    public async Task Cash_out_is_blocked_until_KYC_is_verified()
    {
        var host = await _h.AddUserAsync(UserRole.Host);
        await CreditHostAsync(host.Id, 1000m);

        var act = () => _h.Wallets.RequestCashOutAsync(host.Id, 600m, "co-1");

        await act.Should().ThrowAsync<DomainException>().WithMessage("*Verify your identity*");
    }

    [Fact]
    public async Task Cash_out_is_blocked_while_the_identity_check_is_still_being_reviewed()
    {
        var host = await _h.AddUserAsync(UserRole.Host, KycStatus.Pending);
        await CreditHostAsync(host.Id, 1000m);

        var act = () => _h.Wallets.RequestCashOutAsync(host.Id, 600m, "co-1");

        // A host waiting on a reviewer is told to wait, not told to do something they have
        // already done — the two refusals look identical from the wallet and are not.
        await act.Should().ThrowAsync<DomainException>().WithMessage("*still being reviewed*");
    }

    [Fact]
    public async Task Cash_out_is_blocked_when_the_trust_score_has_collapsed()
    {
        var host = await _h.AddUserAsync(UserRole.Host, KycStatus.Verified);
        await CreditHostAsync(host.Id, 2000m);

        var tracked = await _h.Db.Users.FirstAsync(u => u.Id == host.Id);
        tracked.TrustScore = 20;
        await _h.Db.SaveChangesAsync();

        var act = () => _h.Wallets.RequestCashOutAsync(host.Id, 600m, "co-1");

        // The score gates this and nothing else, which is the point of having one: money leaving
        // the platform is the movement that cannot be undone cheaply.
        await act.Should().ThrowAsync<DomainException>().WithMessage("*under review*");
    }

    [Fact]
    public async Task One_bad_rating_does_not_strand_a_host_s_earnings()
    {
        var host = await _h.AddUserAsync(UserRole.Host, KycStatus.Verified);
        await CreditHostAsync(host.Id, 2000m);

        // The worst a single counterparty can do is six points. The floor sits far below that on
        // purpose: an annoyed renter must not be able to freeze someone's income.
        var tracked = await _h.Db.Users.FirstAsync(u => u.Id == host.Id);
        tracked.TrustScore = 100 - 6;
        await _h.Db.SaveChangesAsync();

        var payout = await _h.Wallets.RequestCashOutAsync(host.Id, 600m, "co-1");

        payout.Status.Should().Be(PayoutStatus.Requested);
    }

    [Fact]
    public async Task A_successful_cash_out_debits_the_earning_balance_immediately()
    {
        var host = await _h.AddUserAsync(UserRole.Host, KycStatus.Verified);
        var credited = await CreditHostAsync(host.Id, 2000m);
        credited.Should().Be(1800m);

        var payout = await _h.Wallets.RequestCashOutAsync(host.Id, 600m, "co-1");

        payout.Status.Should().Be(PayoutStatus.Requested);

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(host.Id);
        wallet.EarningBalance.Should().Be(1200m);
    }

    [Fact]
    public async Task A_paid_payout_posts_nothing_further_to_the_ledger()
    {
        var host = await _h.AddUserAsync(UserRole.Host, KycStatus.Verified);
        await CreditHostAsync(host.Id, 2000m);
        var payout = await _h.Wallets.RequestCashOutAsync(host.Id, 600m, "co-1");
        var before = await _h.Db.LedgerTransactions.CountAsync();

        var completed = await _h.Wallets.CompletePayoutAsync(payout.Id, "pout_abc");

        completed.Status.Should().Be(PayoutStatus.Paid);
        completed.ProviderReference.Should().Be("pout_abc");
        (await _h.Db.LedgerTransactions.CountAsync()).Should().Be(before,
            "the debit was posted when the payout was requested; success is the end of that movement");

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(host.Id);
        wallet.EarningBalance.Should().Be(1200m);
    }

    [Fact]
    public async Task A_rejected_payout_returns_the_credits_to_the_host()
    {
        var host = await _h.AddUserAsync(UserRole.Host, KycStatus.Verified);
        await CreditHostAsync(host.Id, 2000m);
        var payout = await _h.Wallets.RequestCashOutAsync(host.Id, 600m, "co-1");

        var failed = await _h.Wallets.FailPayoutAsync(payout.Id, "Bank account closed.");

        failed.Status.Should().Be(PayoutStatus.Failed);
        failed.FailureReason.Should().Be("Bank account closed.");

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(host.Id);
        wallet.EarningBalance.Should().Be(1800m, "the host is whole again");
    }

    [Fact]
    public async Task The_refund_is_a_compensating_transaction_not_an_erasure()
    {
        var host = await _h.AddUserAsync(UserRole.Host, KycStatus.Verified);
        await CreditHostAsync(host.Id, 2000m);
        var payout = await _h.Wallets.RequestCashOutAsync(host.Id, 600m, "co-1");

        await _h.Wallets.FailPayoutAsync(payout.Id, "Bank account closed.");

        var types = await _h.Db.LedgerTransactions.Select(t => t.Type).ToListAsync();
        types.Should().Contain(LedgerTransactionType.Payout, "the failed attempt stays in the history");
        types.Should().Contain(LedgerTransactionType.Refund);
    }

    [Fact]
    public async Task A_refunded_payout_leaves_the_wallet_reconciling_against_its_ledger()
    {
        var host = await _h.AddUserAsync(UserRole.Host, KycStatus.Verified);
        await CreditHostAsync(host.Id, 2000m);
        var payout = await _h.Wallets.RequestCashOutAsync(host.Id, 600m, "co-1");

        await _h.Wallets.FailPayoutAsync(payout.Id, "Bank account closed.");

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(host.Id);
        var replayed = await _h.Ledger.RecomputeFromEntriesAsync(wallet.Id);

        replayed.Earning.Should().Be(wallet.EarningBalance);
        replayed.Spendable.Should().Be(wallet.SpendableBalance);
        replayed.Held.Should().Be(wallet.HeldBalance);
    }

    [Fact]
    public async Task Failing_the_same_payout_twice_refunds_once()
    {
        var host = await _h.AddUserAsync(UserRole.Host, KycStatus.Verified);
        await CreditHostAsync(host.Id, 2000m);
        var payout = await _h.Wallets.RequestCashOutAsync(host.Id, 600m, "co-1");

        await _h.Wallets.FailPayoutAsync(payout.Id, "Bank account closed.");
        await _h.Wallets.FailPayoutAsync(payout.Id, "Bank account closed.");

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(host.Id);
        wallet.EarningBalance.Should().Be(1800m, "an aggregator retrying its callback must not pay twice");
        (await _h.Db.LedgerTransactions.CountAsync(t => t.Type == LedgerTransactionType.Refund))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_refunded_payout_cannot_later_be_marked_paid()
    {
        var host = await _h.AddUserAsync(UserRole.Host, KycStatus.Verified);
        await CreditHostAsync(host.Id, 2000m);
        var payout = await _h.Wallets.RequestCashOutAsync(host.Id, 600m, "co-1");
        await _h.Wallets.FailPayoutAsync(payout.Id, "Bank account closed.");

        var act = () => _h.Wallets.CompletePayoutAsync(payout.Id, "pout_abc");

        await act.Should().ThrowAsync<DomainException>().WithMessage("*already refunded*");
    }

    [Fact]
    public async Task A_paid_payout_cannot_be_failed_after_the_fact()
    {
        var host = await _h.AddUserAsync(UserRole.Host, KycStatus.Verified);
        await CreditHostAsync(host.Id, 2000m);
        var payout = await _h.Wallets.RequestCashOutAsync(host.Id, 600m, "co-1");
        await _h.Wallets.CompletePayoutAsync(payout.Id, "pout_abc");

        var act = () => _h.Wallets.FailPayoutAsync(payout.Id, "too late");

        await act.Should().ThrowAsync<DomainException>().WithMessage("*already paid*");

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(host.Id);
        wallet.EarningBalance.Should().Be(1200m, "money that has left cannot be conjured back by a status flip");
    }

    [Fact]
    public async Task Recharge_below_the_minimum_is_rejected()
    {
        var renter = await _h.AddUserAsync(UserRole.Renter);

        var act = () => _h.Wallets.RechargeAsync(renter.Id, 50m, "r1");

        await act.Should().ThrowAsync<DomainException>().WithMessage("*Minimum recharge*");
    }

    /// <summary>
    /// Puts credits into a host's earning bucket the only legitimate way — via a settlement.
    /// Returns what the host actually received, i.e. <paramref name="grossCredits"/> less commission.
    /// </summary>
    private async Task<decimal> CreditHostAsync(Guid hostId, decimal grossCredits)
    {
        var renter = await _h.AddUserAsync(UserRole.Renter);
        var bookingId = Guid.NewGuid();

        await _h.Wallets.RechargeAsync(renter.Id, grossCredits, $"fund:{bookingId}");
        await _h.Wallets.PlaceHoldAsync(renter.Id, bookingId, grossCredits, $"hold:{bookingId}");

        var settlement = await _h.Wallets.SettleAsync(new ParkNest.Application.Wallets.SettlementRequest(
            bookingId, renter.Id, hostId, grossCredits, $"settle:{bookingId}"));

        // AddUserAsync signed the throwaway renter in; put the host back so the caller's
        // subsequent cash-out runs as the host.
        _h.CurrentUser.SignIn(hostId, UserRole.Host);

        return settlement.HostCredited;
    }
}
