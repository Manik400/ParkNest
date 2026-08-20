using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ParkNest.Application.Bookings;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

/// <summary>
/// The meter charges an over-run while it is happening. Most of these tests are about it agreeing
/// with checkout: a session must cost the same whether the meter ran every increment, once, or
/// never, and no credit may be taken twice.
/// </summary>
public sealed class OverstayMeterTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly IOverstayMeter _meter;

    public OverstayMeterTests()
    {
        _meter = new OverstayMeter(
            _h.Db, _h.Wallets, _h.PricingService, _h.Clock,
            Microsoft.Extensions.Options.Options.Create(_h.Options),
            _h.Events,
            NullLogger<OverstayMeter>.Instance);
    }

    public void Dispose() => _h.Dispose();

    /// <summary>A one-hour booking, started, at 60/hr with a flat overstay rate.</summary>
    private async Task<(Guid RenterId, Guid HostId, Guid BookingId)> RunningSessionAsync(
        decimal rechargeAmount = 1000m,
        decimal overstayMultiplier = 1.0m)
    {
        var renter = await _h.AddUserAsync(UserRole.Renter);
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync(overstayMultiplier: overstayMultiplier);

        var space = await _h.AddPublishedSpaceAsync(host.Id, 60m);
        var vehicle = await _h.AddVehicleAsync(renter.Id);
        await _h.Wallets.RechargeAsync(renter.Id, rechargeAmount, $"recharge:{renter.Id}");

        _h.CurrentUser.SignIn(renter.Id);

        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            space.Id, vehicle.Id, TestHarness.Origin, 60, $"bk-{Guid.NewGuid()}"));

        await _h.Bookings.StartSessionAsync(booking.Id, DetectionMethod.AppConfirmed, TestHarness.Origin);

        return (renter.Id, host.Id, booking.Id);
    }

    private Task<Domain.Bookings.Booking> ReloadAsync(Guid bookingId) =>
        _h.Db.Bookings.FindAsync(bookingId).AsTask()!;

    [Fact]
    public async Task A_session_still_inside_its_window_is_not_touched()
    {
        await RunningSessionAsync();

        _h.Clock.Advance(TimeSpan.FromMinutes(30));
        var billed = await _meter.MeterOpenSessionsAsync();

        billed.Should().Be(0);
    }

    [Fact]
    public async Task A_session_inside_the_grace_period_is_not_touched()
    {
        // Two minutes late, with a fifteen minute grace. The renter is walking back to the car.
        var (_, _, bookingId) = await RunningSessionAsync();

        _h.Clock.Advance(TimeSpan.FromMinutes(62));
        var billed = await _meter.MeterOpenSessionsAsync();

        billed.Should().Be(0);
        (await ReloadAsync(bookingId)).OverstayAmount.Should().Be(0m);
    }

    [Fact]
    public async Task An_over_run_past_grace_is_billed_while_the_session_is_still_running()
    {
        var (renterId, _, bookingId) = await RunningSessionAsync();

        // 90 minutes into a 60 minute booking: 30 minutes over, well past grace.
        _h.Clock.Advance(TimeSpan.FromMinutes(90));
        var billed = await _meter.MeterOpenSessionsAsync();

        billed.Should().Be(1);

        var booking = await ReloadAsync(bookingId);
        booking.OverstayAmount.Should().Be(30m);
        booking.Status.Should().Be(BookingStatus.Active, "the car is still there; the session is not over");

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(renterId);
        wallet.HeldBalance.Should().Be(90m, "the overstay is pulled into the hold, not spent");
    }

    [Fact]
    public async Task Running_the_meter_again_with_no_time_passed_charges_nothing_more()
    {
        var (renterId, _, bookingId) = await RunningSessionAsync();

        _h.Clock.Advance(TimeSpan.FromMinutes(90));
        await _meter.MeterOpenSessionsAsync();
        var second = await _meter.MeterOpenSessionsAsync();

        second.Should().Be(0);
        (await ReloadAsync(bookingId)).OverstayAmount.Should().Be(30m);
        (await _h.Wallets.GetOrCreateWalletAsync(renterId)).HeldBalance.Should().Be(90m);
    }

    [Fact]
    public async Task Each_further_increment_is_charged_as_it_passes()
    {
        var (_, _, bookingId) = await RunningSessionAsync();

        _h.Clock.Advance(TimeSpan.FromMinutes(90));
        await _meter.MeterOpenSessionsAsync();
        (await ReloadAsync(bookingId)).OverstayAmount.Should().Be(30m);

        _h.Clock.Advance(TimeSpan.FromMinutes(30));
        await _meter.MeterOpenSessionsAsync();
        (await ReloadAsync(bookingId)).OverstayAmount.Should().Be(60m);
    }

    [Fact]
    public async Task A_metered_session_costs_the_same_at_checkout_as_an_unmetered_one()
    {
        // The property that matters most: metering changes when credits move, never how many.
        var (renterId, hostId, bookingId) = await RunningSessionAsync();

        _h.Clock.Advance(TimeSpan.FromMinutes(90));
        await _meter.MeterOpenSessionsAsync();
        _h.Clock.Advance(TimeSpan.FromMinutes(30));
        await _meter.MeterOpenSessionsAsync();

        var outcome = await _h.Bookings.EndSessionAsync(
            bookingId, DetectionMethod.AppConfirmed, _h.Clock.UtcNow);

        // Two hours of a one-hour booking at 60/hr: 60 booked + 60 overstay.
        outcome.BilledMinutes.Should().Be(120);
        outcome.TotalCharged.Should().Be(120m);
        outcome.Shortfall.Should().Be(0m);
        outcome.Booking.Status.Should().Be(BookingStatus.Completed);

        var renter = await _h.Wallets.GetOrCreateWalletAsync(renterId);
        renter.SpendableBalance.Should().Be(880m);
        renter.HeldBalance.Should().Be(0m);

        var host = await _h.Wallets.GetOrCreateWalletAsync(hostId);
        host.EarningBalance.Should().Be(108m);
    }

    [Fact]
    public async Task A_metered_session_still_reconciles_against_its_ledger()
    {
        var (renterId, hostId, bookingId) = await RunningSessionAsync();

        _h.Clock.Advance(TimeSpan.FromMinutes(95));
        await _meter.MeterOpenSessionsAsync();
        await _h.Bookings.EndSessionAsync(bookingId, DetectionMethod.AppConfirmed, _h.Clock.UtcNow);

        foreach (var userId in new[] { renterId, hostId })
        {
            var wallet = await _h.Wallets.GetOrCreateWalletAsync(userId);
            var replayed = await _h.Ledger.RecomputeFromEntriesAsync(wallet.Id);

            replayed.Spendable.Should().Be(wallet.SpendableBalance);
            replayed.Held.Should().Be(wallet.HeldBalance);
            replayed.Earning.Should().Be(wallet.EarningBalance);
        }
    }

    [Fact]
    public async Task The_overstay_multiplier_is_applied_by_the_meter_too()
    {
        var (_, _, bookingId) = await RunningSessionAsync(overstayMultiplier: 1.5m);

        _h.Clock.Advance(TimeSpan.FromMinutes(120));
        await _meter.MeterOpenSessionsAsync();

        // 60 minutes over at 90/hr.
        (await ReloadAsync(bookingId)).OverstayAmount.Should().Be(90m);
    }

    [Fact]
    public async Task A_renter_who_runs_out_mid_session_is_not_ended_by_the_meter()
    {
        // 100 credits: 60 goes to the hold, leaving 40 against a 60 over-run.
        var (renterId, _, bookingId) = await RunningSessionAsync(rechargeAmount: 100m);

        _h.Clock.Advance(TimeSpan.FromMinutes(120));
        await _meter.MeterOpenSessionsAsync();

        var booking = await ReloadAsync(bookingId);
        booking.Status.Should().Be(BookingStatus.Active,
            "the session is still running and the renter can still top up");
        booking.OverstayAmount.Should().Be(40m, "only what could actually be covered");

        (await _h.Wallets.GetOrCreateWalletAsync(renterId)).SpendableBalance.Should().Be(0m);
    }

    [Fact]
    public async Task A_shortfall_the_meter_saw_is_forgiven_if_the_renter_tops_up_before_leaving()
    {
        // The reason the meter must not decide violations: it only knows the balance at that
        // instant, and the renter has until checkout to fix it.
        var (renterId, _, bookingId) = await RunningSessionAsync(rechargeAmount: 100m);

        _h.Clock.Advance(TimeSpan.FromMinutes(120));
        await _meter.MeterOpenSessionsAsync();

        await _h.Wallets.RechargeAsync(renterId, 500m, "top-up-1");

        var outcome = await _h.Bookings.EndSessionAsync(
            bookingId, DetectionMethod.AppConfirmed, _h.Clock.UtcNow);

        outcome.Shortfall.Should().Be(0m);
        outcome.Booking.Status.Should().Be(BookingStatus.Completed);
    }

    [Fact]
    public async Task A_shortfall_that_is_never_covered_still_ends_as_a_violation()
    {
        var (_, hostId, bookingId) = await RunningSessionAsync(rechargeAmount: 100m);

        _h.Clock.Advance(TimeSpan.FromMinutes(120));
        await _meter.MeterOpenSessionsAsync();

        var outcome = await _h.Bookings.EndSessionAsync(
            bookingId, DetectionMethod.AppConfirmed, _h.Clock.UtcNow);

        // 120 billed minutes at 60/hr is 120 owed against 100 recharged.
        outcome.Shortfall.Should().Be(20m);
        outcome.Booking.Status.Should().Be(BookingStatus.InViolation);

        // The host still receives everything that could be collected, metered or not.
        var host = await _h.Wallets.GetOrCreateWalletAsync(hostId);
        host.EarningBalance.Should().Be(90m);
    }

    [Fact]
    public async Task A_session_that_never_started_is_ignored()
    {
        // Held, not Active: nobody is parked, so there is nothing to over-run.
        var renter = await _h.AddUserAsync(UserRole.Renter);
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();
        var space = await _h.AddPublishedSpaceAsync(host.Id, 60m);
        var vehicle = await _h.AddVehicleAsync(renter.Id);
        await _h.Wallets.RechargeAsync(renter.Id, 1000m, $"recharge:{renter.Id}");
        _h.CurrentUser.SignIn(renter.Id);

        await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            space.Id, vehicle.Id, TestHarness.Origin, 60, "bk-held"));

        _h.Clock.Advance(TimeSpan.FromMinutes(200));

        (await _meter.MeterOpenSessionsAsync()).Should().Be(0);
    }
}
