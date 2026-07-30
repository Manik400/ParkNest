using FluentAssertions;
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

        await act.Should().ThrowAsync<DomainException>().WithMessage("*KYC*");
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
