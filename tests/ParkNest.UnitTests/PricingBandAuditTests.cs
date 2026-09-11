using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ParkNest.Application.Pricing;
using ParkNest.Domain.Common;
using ParkNest.Domain.Pricing;

namespace ParkNest.UnitTests;

/// <summary>
/// Band management is the one screen that changes what every host in a city may charge, without a
/// deploy and therefore without a commit. These tests are about the record it leaves behind.
/// </summary>
public sealed class PricingBandAuditTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly PricingBandAdminService _bands;
    private readonly Guid _admin = Guid.NewGuid();

    public PricingBandAuditTests()
    {
        _bands = new PricingBandAdminService(
            _h.Db, _h.Clock, _h.CurrentUser, Options.Create(_h.Options));

        _h.CurrentUser.SignIn(_admin, UserRole.Admin);
    }

    public void Dispose() => _h.Dispose();

    private static UpsertPricingBand Band(
        decimal min = 20m,
        decimal max = 80m,
        decimal overstay = 1.5m,
        bool active = true,
        string? reason = null) =>
        new("Bengaluru", "Indiranagar", VehicleType.FourWheeler, min, max, overstay, active, reason);

    [Fact]
    public async Task Creating_a_band_records_who_created_it_and_no_previous_values()
    {
        await _bands.UpsertAsync(Band(reason: "Launching Indiranagar"));

        var change = await _h.Db.PricingBandChanges.SingleAsync();
        change.Kind.Should().Be(PricingBandChangeKind.Created);
        change.ChangedByUserId.Should().Be(_admin);
        change.Reason.Should().Be("Launching Indiranagar");
        change.MinPricePerHour.Should().Be(20m);
        change.MaxPricePerHour.Should().Be(80m);

        // There was no band before, so claiming a previous value would be inventing one.
        change.PreviousMinPricePerHour.Should().BeNull();
        change.PreviousIsActive.Should().BeNull();
    }

    [Fact]
    public async Task Editing_a_band_records_both_sides_of_the_change()
    {
        await _bands.UpsertAsync(Band(min: 20m, max: 80m));
        _h.Clock.Advance(TimeSpan.FromDays(30));
        await _bands.UpsertAsync(Band(min: 30m, max: 120m, reason: "Festival demand"));

        var change = await _h.Db.PricingBandChanges
            .OrderByDescending(c => c.ChangedAt)
            .FirstAsync();

        change.Kind.Should().Be(PricingBandChangeKind.Updated);
        change.PreviousMinPricePerHour.Should().Be(20m);
        change.PreviousMaxPricePerHour.Should().Be(80m);
        change.MinPricePerHour.Should().Be(30m);
        change.MaxPricePerHour.Should().Be(120m);

        // The band itself holds only the current state; the history is where the old numbers live.
        var band = await _h.Db.CityPricingConfigs.SingleAsync();
        band.MinPricePerHour.Should().Be(30m);
        (await _h.Db.PricingBandChanges.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Deactivating_is_recorded_as_its_own_kind_rather_than_an_edit()
    {
        var band = await _bands.UpsertAsync(Band());

        await _bands.SetActiveAsync(band.Id, isActive: false, reason: "Zone under review");

        var change = await _h.Db.PricingBandChanges
            .OrderByDescending(c => c.ChangedAt)
            .FirstAsync();

        // Findable without reading every row's before-and-after — switching a band off stops
        // every listing in that zone validating, which is the most disruptive edit on the screen.
        change.Kind.Should().Be(PricingBandChangeKind.Deactivated);
        change.PreviousIsActive.Should().BeTrue();
        change.IsActive.Should().BeFalse();

        // The prices survive the deactivation, so "what did this zone used to allow" is answerable.
        change.MinPricePerHour.Should().Be(20m);
    }

    [Fact]
    public async Task Setting_the_state_it_is_already_in_records_nothing()
    {
        var band = await _bands.UpsertAsync(Band());
        var before = await _h.Db.PricingBandChanges.CountAsync();

        await _bands.SetActiveAsync(band.Id, isActive: true, reason: "no-op");

        (await _h.Db.PricingBandChanges.CountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task The_history_survives_the_band_being_renamed_out_from_under_it()
    {
        await _bands.UpsertAsync(Band());

        var change = await _h.Db.PricingBandChanges.SingleAsync();

        // City and zone are copied onto the audit row rather than joined to the band, so the row
        // still says which band it described even if that band later moves or is deleted.
        change.City.Should().Be("Bengaluru");
        change.Zone.Should().Be("Indiranagar");
        change.VehicleType.Should().Be(VehicleType.FourWheeler);
    }

    [Fact]
    public async Task A_blank_zone_is_the_city_wide_band_not_a_second_one()
    {
        await _bands.UpsertAsync(new UpsertPricingBand("Bengaluru", null, VehicleType.FourWheeler, 20m, 80m));
        await _bands.UpsertAsync(new UpsertPricingBand("Bengaluru", "   ", VehicleType.FourWheeler, 25m, 90m));

        // Two rows here would mean a shadow band that a stray space in the form had created, and
        // whichever one GetBandAsync happened to pick would be the price ceiling for that city.
        var bands = await _h.Db.CityPricingConfigs.ToListAsync();
        bands.Should().HaveCount(1);
        bands[0].Zone.Should().BeNull();
        bands[0].MinPricePerHour.Should().Be(25m);
    }

    [Fact]
    public async Task An_overstay_multiplier_above_the_platform_ceiling_is_refused_at_write_time()
    {
        var beyond = _h.Options.MaxOverstayMultiplier + 0.5m;

        var act = () => _bands.UpsertAsync(Band(overstay: beyond));

        // Refused here rather than clamped at read time, so the operator finds out now instead of
        // a renter finding out at their next over-run.
        await act.Should().ThrowAsync<DomainException>();
        (await _h.Db.CityPricingConfigs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_overstay_multiplier_below_one_is_refused()
    {
        var act = () => _bands.UpsertAsync(Band(overstay: 0.5m));

        // Under 1.0 an over-run is cheaper per minute than the booking, which pays people to
        // overstay.
        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task A_minimum_above_the_maximum_is_refused()
    {
        var act = () => _bands.UpsertAsync(Band(min: 100m, max: 50m));

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task History_is_newest_first_and_can_be_scoped_to_one_band()
    {
        var first = await _bands.UpsertAsync(Band(min: 20m));
        _h.Clock.Advance(TimeSpan.FromHours(1));
        await _bands.UpsertAsync(Band(min: 30m));
        _h.Clock.Advance(TimeSpan.FromHours(1));
        await _bands.UpsertAsync(new UpsertPricingBand("Pune", null, VehicleType.FourWheeler, 10m, 40m));

        var all = await _bands.HistoryAsync(bandId: null);
        all.Should().HaveCount(3);
        all[0].City.Should().Be("Pune");

        var scoped = await _bands.HistoryAsync(first.Id);
        scoped.Should().HaveCount(2);
        scoped.Should().OnlyContain(c => c.CityPricingConfigId == first.Id);
        scoped[0].MinPricePerHour.Should().Be(30m);
    }

    [Fact]
    public async Task A_non_admin_cannot_read_or_change_bands()
    {
        _h.CurrentUser.SignIn(Guid.NewGuid(), UserRole.Both);

        await new Func<Task>(() => _bands.UpsertAsync(Band()))
            .Should().ThrowAsync<ForbiddenException>();
        await new Func<Task>(() => _bands.ListAsync(null))
            .Should().ThrowAsync<ForbiddenException>();
        await new Func<Task>(() => _bands.HistoryAsync(null))
            .Should().ThrowAsync<ForbiddenException>();
    }
}
