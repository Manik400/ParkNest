using FluentAssertions;
using ParkNest.Application.Bookings;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

/// <summary>
/// The scenarios PRD §5.1 exists to solve: over-runs bill themselves, and a renter who cannot
/// cover the over-run loses access rather than leaving the host chasing cash.
/// </summary>
public sealed class BookingSettlementTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task<(Guid renterId, Guid hostId, Guid spaceId, Guid vehicleId)> SetupAsync(
        decimal rechargeAmount = 1000m,
        decimal pricePerHour = 60m,
        decimal overstayMultiplier = 1.0m)
    {
        var renter = await _h.AddUserAsync(UserRole.Renter);
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync(overstayMultiplier: overstayMultiplier);

        var space = await _h.AddPublishedSpaceAsync(host.Id, pricePerHour);
        var vehicle = await _h.AddVehicleAsync(renter.Id);

        await _h.Wallets.RechargeAsync(renter.Id, rechargeAmount, $"recharge:{renter.Id}");

        return (renter.Id, host.Id, space.Id, vehicle.Id);
    }

    [Fact]
    public async Task Booking_reserves_credits_up_front_and_moves_them_out_of_spendable()
    {
        var (renterId, _, spaceId, vehicleId) = await SetupAsync();

        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            renterId, spaceId, vehicleId, TestHarness.Origin, 60, "bk-1"));

        booking.HoldAmount.Should().Be(60m);
        booking.Status.Should().Be(BookingStatus.Held);

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(renterId);
        wallet.SpendableBalance.Should().Be(940m);
        wallet.HeldBalance.Should().Be(60m);
    }

    [Fact]
    public async Task Booking_is_refused_when_the_renter_cannot_cover_it()
    {
        var (renterId, _, spaceId, vehicleId) = await SetupAsync(rechargeAmount: 100m);

        var act = () => _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            renterId, spaceId, vehicleId, TestHarness.Origin, 240, "bk-1"));

        await act.Should().ThrowAsync<InsufficientCreditsException>();

        // No booking may exist without its hold.
        _h.Db.Bookings.Should().BeEmpty();
    }

    [Fact]
    public async Task Leaving_early_releases_the_unused_hold_back_to_the_renter()
    {
        var (renterId, hostId, spaceId, vehicleId) = await SetupAsync();

        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            renterId, spaceId, vehicleId, TestHarness.Origin, 120, "bk-1"));
        booking.HoldAmount.Should().Be(120m);

        await _h.Bookings.StartSessionAsync(booking.Id, DetectionMethod.AppConfirmed, TestHarness.Origin);

        // Out after 45 minutes of a 2-hour booking.
        var outcome = await _h.Bookings.EndSessionAsync(
            booking.Id, DetectionMethod.AppConfirmed, TestHarness.Origin.AddMinutes(45));

        outcome.BilledMinutes.Should().Be(45);
        outcome.TotalCharged.Should().Be(45m);
        outcome.ReleasedToRenter.Should().Be(75m);
        outcome.Shortfall.Should().Be(0m);
        outcome.Booking.Status.Should().Be(BookingStatus.Completed);

        var renterWallet = await _h.Wallets.GetOrCreateWalletAsync(renterId);
        renterWallet.SpendableBalance.Should().Be(955m);
        renterWallet.HeldBalance.Should().Be(0m);

        var hostWallet = await _h.Wallets.GetOrCreateWalletAsync(hostId);
        hostWallet.EarningBalance.Should().Be(40.50m);
    }

    [Fact]
    public async Task Overstaying_auto_debits_the_extra_time_without_asking_anyone()
    {
        var (renterId, hostId, spaceId, vehicleId) = await SetupAsync();

        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            renterId, spaceId, vehicleId, TestHarness.Origin, 60, "bk-1"));

        await _h.Bookings.StartSessionAsync(booking.Id, DetectionMethod.AppConfirmed, TestHarness.Origin);

        // Booked 1 hour, stayed 1h50m — bills out at 2 hours after rounding up.
        var outcome = await _h.Bookings.EndSessionAsync(
            booking.Id, DetectionMethod.AppConfirmed, TestHarness.Origin.AddMinutes(110));

        outcome.BilledMinutes.Should().Be(120);
        outcome.TotalCharged.Should().Be(120m);
        outcome.Shortfall.Should().Be(0m);
        outcome.Booking.Status.Should().Be(BookingStatus.Completed);
        outcome.Booking.OverstayAmount.Should().Be(60m);

        var renterWallet = await _h.Wallets.GetOrCreateWalletAsync(renterId);
        renterWallet.SpendableBalance.Should().Be(880m);
        renterWallet.HeldBalance.Should().Be(0m);

        var hostWallet = await _h.Wallets.GetOrCreateWalletAsync(hostId);
        hostWallet.EarningBalance.Should().Be(108m);
    }

    [Fact]
    public async Task Overstay_premium_multiplier_is_applied_to_the_extra_minutes_only()
    {
        var (renterId, _, spaceId, vehicleId) = await SetupAsync(overstayMultiplier: 1.5m);

        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            renterId, spaceId, vehicleId, TestHarness.Origin, 60, "bk-1"));
        await _h.Bookings.StartSessionAsync(booking.Id, DetectionMethod.AppConfirmed, TestHarness.Origin);

        var outcome = await _h.Bookings.EndSessionAsync(
            booking.Id, DetectionMethod.AppConfirmed, TestHarness.Origin.AddMinutes(120));

        // 60 booked minutes at 60/hr + 60 overstay minutes at 90/hr.
        outcome.Booking.OverstayAmount.Should().Be(90m);
        outcome.TotalCharged.Should().Be(150m);
    }

    [Fact]
    public async Task An_uncovered_overstay_flags_a_violation_instead_of_chasing_payment()
    {
        // Just enough to book an hour, nothing left for the over-run.
        var (renterId, hostId, spaceId, vehicleId) = await SetupAsync(rechargeAmount: 100m);

        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            renterId, spaceId, vehicleId, TestHarness.Origin, 60, "bk-1"));
        await _h.Bookings.StartSessionAsync(booking.Id, DetectionMethod.AppConfirmed, TestHarness.Origin);

        // 3 hours parked: 60 held, 120 more owed, only 40 spendable left.
        var outcome = await _h.Bookings.EndSessionAsync(
            booking.Id, DetectionMethod.AppConfirmed, TestHarness.Origin.AddMinutes(180));

        outcome.Shortfall.Should().Be(80m);
        outcome.TotalCharged.Should().Be(100m);
        outcome.Booking.Status.Should().Be(BookingStatus.InViolation);

        // The host still receives everything that could actually be collected.
        var hostWallet = await _h.Wallets.GetOrCreateWalletAsync(hostId);
        hostWallet.EarningBalance.Should().Be(90m);

        var renterWallet = await _h.Wallets.GetOrCreateWalletAsync(renterId);
        renterWallet.SpendableBalance.Should().Be(0m);

        var renter = await _h.Db.Users.FindAsync(renterId);
        renter!.TrustScore.Should().Be(90);
    }

    [Fact]
    public async Task Cancelling_before_the_session_starts_returns_the_whole_hold()
    {
        var (renterId, _, spaceId, vehicleId) = await SetupAsync();

        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            renterId, spaceId, vehicleId, TestHarness.Origin, 120, "bk-1"));

        await _h.Bookings.CancelBookingAsync(booking.Id);

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(renterId);
        wallet.SpendableBalance.Should().Be(1000m);
        wallet.HeldBalance.Should().Be(0m);
    }

    [Fact]
    public async Task A_space_cannot_be_double_booked_for_an_overlapping_window()
    {
        var (renterId, _, spaceId, vehicleId) = await SetupAsync();

        await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            renterId, spaceId, vehicleId, TestHarness.Origin, 120, "bk-1"));

        var act = () => _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            renterId, spaceId, vehicleId, TestHarness.Origin.AddMinutes(60), 60, "bk-2"));

        await act.Should().ThrowAsync<DomainException>()
            .WithMessage("*already booked*");
    }

    [Fact]
    public async Task Retrying_a_booking_request_does_not_create_a_second_booking()
    {
        var (renterId, _, spaceId, vehicleId) = await SetupAsync();

        var request = new CreateBookingRequest(renterId, spaceId, vehicleId, TestHarness.Origin, 60, "bk-retry");

        var first = await _h.Bookings.CreateBookingAsync(request);
        var second = await _h.Bookings.CreateBookingAsync(request);

        second.Id.Should().Be(first.Id);

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(renterId);
        wallet.HeldBalance.Should().Be(60m);
    }
}
