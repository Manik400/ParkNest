using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Bookings;
using ParkNest.Application.Notifications;
using ParkNest.Domain.Bookings;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

/// <summary>
/// The case where an over-run stops being only the over-running renter's problem: their car is
/// still in a bay somebody else has booked, and that renter is driving to it.
///
/// The credit system has always billed the overstay, which settles the money and settles nothing
/// else. What these tests pin down is the half that does not depend on a compensation policy: all
/// three parties hear about it while it can still be acted on, and the renter who was turned away
/// is not charged a late-cancellation fee for a slot that was never available to them.
/// </summary>
public sealed class BlockedSlotTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly IOverstayMeter _meter;

    public BlockedSlotTests()
    {
        _meter = new OverstayMeter(
            _h.Db, _h.Wallets, _h.PricingService, _h.Clock,
            Microsoft.Extensions.Options.Options.Create(_h.Options),
            _h.Events,
            NullLogger<OverstayMeter>.Instance);
    }

    public void Dispose() => _h.Dispose();

    private sealed record Scene(
        Guid HostId, Guid SpaceId, Guid StayerId, Guid WaiterId, Guid RunningBookingId, Guid NextBookingId);

    /// <summary>
    /// One space, back-to-back bookings. The first is running; the second starts the moment the
    /// first is due to end, which is exactly the arrangement the overlap constraint permits and
    /// the one that goes wrong when nobody moves their car.
    /// </summary>
    private async Task<Scene> BackToBackAsync(int gapMinutes = 0)
    {
        var host = await _h.AddUserAsync(UserRole.Host);
        var stayer = await _h.AddUserAsync(UserRole.Renter);
        var waiter = await _h.AddUserAsync(UserRole.Renter);
        await _h.AddBandAsync();

        var space = await _h.AddPublishedSpaceAsync(host.Id, 60m);
        var stayerVehicle = await _h.AddVehicleAsync(stayer.Id);
        var waiterVehicle = await _h.AddVehicleAsync(waiter.Id);

        await _h.Wallets.RechargeAsync(stayer.Id, 2000m, $"recharge:{stayer.Id}");
        await _h.Wallets.RechargeAsync(waiter.Id, 2000m, $"recharge:{waiter.Id}");

        _h.CurrentUser.SignIn(stayer.Id);
        var running = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            space.Id, stayerVehicle.Id, TestHarness.Origin, 60, $"bk-{Guid.NewGuid()}"));
        await _h.Bookings.StartSessionAsync(running.Id, DetectionMethod.AppConfirmed, TestHarness.Origin);

        _h.CurrentUser.SignIn(waiter.Id);
        var next = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            space.Id, waiterVehicle.Id, TestHarness.Origin.AddMinutes(60 + gapMinutes), 60, $"bk-{Guid.NewGuid()}"));

        return new Scene(host.Id, space.Id, stayer.Id, waiter.Id, running.Id, next.Id);
    }

    private Task<Booking> ReloadAsync(Guid bookingId) => _h.Db.Bookings.FindAsync(bookingId).AsTask()!;

    [Fact]
    public async Task A_session_sitting_in_the_next_renters_slot_flags_that_booking()
    {
        var scene = await BackToBackAsync();

        // One minute past the first booking's end. Well inside the fifteen minute grace, so
        // nothing has been charged yet — which is the point: grace is about when money moves, and
        // the second renter's slot is occupied either way.
        _h.Clock.Advance(TimeSpan.FromMinutes(61));
        var billed = await _meter.MeterOpenSessionsAsync();

        billed.Should().Be(0);

        var blocked = await ReloadAsync(scene.NextBookingId);
        blocked.BlockedByBookingId.Should().Be(scene.RunningBookingId);
        blocked.BlockedAt.Should().Be(_h.Clock.UtcNow);
        blocked.WasBlocked.Should().BeTrue();
    }

    [Fact]
    public async Task All_three_parties_are_named_on_the_event()
    {
        var scene = await BackToBackAsync();

        _h.Clock.Advance(TimeSpan.FromMinutes(61));
        await _meter.MeterOpenSessionsAsync();

        var published = _h.Events.Single<NextSlotBlocked>();

        published.BlockedBookingId.Should().Be(scene.NextBookingId);
        published.BlockedRenterId.Should().Be(scene.WaiterId);
        published.BlockingBookingId.Should().Be(scene.RunningBookingId);
        published.BlockingRenterId.Should().Be(scene.StayerId);
        published.HostId.Should().Be(scene.HostId);
        published.ParkingSpaceId.Should().Be(scene.SpaceId);
        published.BlockedStartTime.Should().Be(TestHarness.Origin.AddMinutes(60));
    }

    [Fact]
    public async Task The_warning_is_sent_once_however_often_the_meter_runs()
    {
        await BackToBackAsync();

        _h.Clock.Advance(TimeSpan.FromMinutes(61));
        await _meter.MeterOpenSessionsAsync();

        // The meter runs every minute. A renter who gets the same warning sixty times stops
        // reading any of them, and the one that mattered is the first.
        _h.Clock.Advance(TimeSpan.FromMinutes(30));
        await _meter.MeterOpenSessionsAsync();
        _h.Clock.Advance(TimeSpan.FromMinutes(30));
        await _meter.MeterOpenSessionsAsync();

        _h.Events.OfType<NextSlotBlocked>().Should().HaveCount(1);
    }

    [Fact]
    public async Task A_booking_still_beyond_the_lookahead_is_left_alone()
    {
        // A two hour gap after the running booking: the over-run is real, but the next renter has
        // not set off and telling them their space is occupied would be a false alarm.
        var scene = await BackToBackAsync(gapMinutes: 120);

        _h.Clock.Advance(TimeSpan.FromMinutes(61));
        await _meter.MeterOpenSessionsAsync();

        (await ReloadAsync(scene.NextBookingId)).WasBlocked.Should().BeFalse();
        _h.Events.Any<NextSlotBlocked>().Should().BeFalse();
    }

    [Fact]
    public async Task A_slot_that_has_been_and_gone_entirely_is_not_flagged()
    {
        var scene = await BackToBackAsync();

        // Three hours on. The second booking's own window closed an hour ago; it was missed, not
        // blocked, and telling that renter to hurry would be absurd.
        _h.Clock.Advance(TimeSpan.FromMinutes(180));
        await _meter.MeterOpenSessionsAsync();

        (await ReloadAsync(scene.NextBookingId)).WasBlocked.Should().BeFalse();
    }

    [Fact]
    public async Task Setting_the_lookahead_to_zero_turns_the_check_off()
    {
        _h.Options.BlockedSlotLookaheadMinutes = 0;
        var scene = await BackToBackAsync();

        _h.Clock.Advance(TimeSpan.FromMinutes(61));
        await _meter.MeterOpenSessionsAsync();

        (await ReloadAsync(scene.NextBookingId)).WasBlocked.Should().BeFalse();
    }

    [Fact]
    public async Task Detection_does_not_disturb_the_metering_it_runs_alongside()
    {
        var scene = await BackToBackAsync();

        // Ninety minutes: thirty over, past grace, so the meter bills as it always did. The point
        // is that adding the check to the same sweep changed neither the figure nor the count.
        _h.Clock.Advance(TimeSpan.FromMinutes(90));
        var billed = await _meter.MeterOpenSessionsAsync();

        billed.Should().Be(1);

        var running = await ReloadAsync(scene.RunningBookingId);
        running.OverstayAmount.Should().Be(30m);

        (await ReloadAsync(scene.NextBookingId)).WasBlocked.Should().BeTrue();
    }

    // --- What it costs the renter who was turned away ---------------------

    [Fact]
    public async Task Cancelling_a_blocked_booking_is_free_however_late_it_is()
    {
        var scene = await BackToBackAsync();

        _h.Clock.Advance(TimeSpan.FromMinutes(61));
        await _meter.MeterOpenSessionsAsync();

        _h.CurrentUser.SignIn(scene.WaiterId);
        var terms = await _h.Bookings.PreviewCancellationAsync(scene.NextBookingId);

        terms.IsFree.Should().BeTrue();
        terms.SlotBlocked.Should().BeTrue();
        terms.Fee.Should().Be(0m);
        terms.Refund.Should().Be(terms.HoldAmount);
    }

    [Fact]
    public async Task The_same_booking_unblocked_would_have_cost_the_late_fee()
    {
        // The control. Without the block, this cancellation is well past the free window and the
        // host is compensated — which is what makes the exemption above a decision rather than an
        // accident of timing.
        var scene = await BackToBackAsync();

        _h.Clock.Advance(TimeSpan.FromMinutes(61));

        _h.CurrentUser.SignIn(scene.WaiterId);
        var terms = await _h.Bookings.PreviewCancellationAsync(scene.NextBookingId);

        terms.IsFree.Should().BeFalse();
        terms.SlotBlocked.Should().BeFalse();
        terms.Fee.Should().Be(30m);
    }

    [Fact]
    public async Task Cancelling_a_blocked_booking_returns_the_whole_hold_and_pays_the_host_nothing()
    {
        var scene = await BackToBackAsync();

        _h.Clock.Advance(TimeSpan.FromMinutes(61));
        await _meter.MeterOpenSessionsAsync();

        _h.CurrentUser.SignIn(scene.WaiterId);
        var before = await _h.Wallets.GetOrCreateWalletAsync(scene.WaiterId);
        var spendableBefore = before.SpendableBalance;
        var heldBefore = before.HeldBalance;

        var outcome = await _h.Bookings.CancelBookingAsync(scene.NextBookingId);

        outcome.Fee.Should().Be(0m);
        outcome.Refund.Should().Be(60m);
        outcome.Booking.Status.Should().Be(BookingStatus.Cancelled);

        var after = await _h.Wallets.GetOrCreateWalletAsync(scene.WaiterId);
        after.SpendableBalance.Should().Be(spendableBefore + 60m);
        after.HeldBalance.Should().Be(heldBefore - 60m);

        // The host is not compensated for a slot they could not deliver, and the ledger has no
        // settlement row to say otherwise.
        (await _h.Wallets.GetOrCreateWalletAsync(scene.HostId)).EarningBalance.Should().Be(0m);
    }

    [Fact]
    public async Task The_block_survives_the_car_finally_leaving()
    {
        var scene = await BackToBackAsync();

        _h.Clock.Advance(TimeSpan.FromMinutes(61));
        await _meter.MeterOpenSessionsAsync();

        // The first renter checks out a few minutes later. The second renter has already been told
        // their slot could not be delivered and that cancelling is free; withdrawing that because
        // the bay came free after they had turned around is worse than never offering it.
        _h.CurrentUser.SignIn(scene.StayerId);
        _h.Clock.Advance(TimeSpan.FromMinutes(4));
        await _h.Bookings.EndSessionAsync(scene.RunningBookingId, DetectionMethod.AppConfirmed);

        _h.CurrentUser.SignIn(scene.WaiterId);
        (await _h.Bookings.PreviewCancellationAsync(scene.NextBookingId)).IsFree.Should().BeTrue();
    }

    // --- What the three of them are actually told -------------------------

    [Fact]
    public async Task Each_party_gets_a_message_written_for_what_they_can_do_about_it()
    {
        var scene = await BackToBackAsync();

        _h.Clock.Advance(TimeSpan.FromMinutes(61));
        await _meter.MeterOpenSessionsAsync();

        var notifications = new NotificationService(
            _h.Db, _h.CurrentUser, _h.Clock, new RecordingPushSender(), NullLogger<NotificationService>.Instance);
        var handlers = new BookingNotificationHandlers(
            notifications, NullLogger<BookingNotificationHandlers>.Instance);

        await handlers.HandleAsync(_h.Events.Single<NextSlotBlocked>());

        var stored = _h.Db.Notifications.ToList();

        var waiter = stored.Single(n => n.UserId == scene.WaiterId);
        waiter.Kind.Should().Be("slot.blocked");
        waiter.Body.Should().Contain("costs you nothing");
        waiter.SubjectId.Should().Be(scene.NextBookingId);

        // The one that gets the car moved says somebody is waiting, not what it cost — the
        // overstay charge already says that, and it has plainly not been persuasive.
        var stayer = stored.Single(n => n.UserId == scene.StayerId);
        stayer.Kind.Should().Be("slot.blocking");
        stayer.Body.Should().Contain("Please move now");
        stayer.SubjectId.Should().Be(scene.RunningBookingId);

        var host = stored.Single(n => n.UserId == scene.HostId);
        host.Kind.Should().Be("slot.blocked");
        host.SubjectId.Should().Be(scene.NextBookingId);
    }
}
