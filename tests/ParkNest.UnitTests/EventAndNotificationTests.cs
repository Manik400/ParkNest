using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Bookings;
using ParkNest.Application.Notifications;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

/// <summary>
/// What the rest of the system hears about, and what the two parties are told.
///
/// The property under test throughout is that publishing is an announcement about something that
/// already happened: the event names facts the booking has finished writing, and a handler that
/// falls over cannot undo any of it.
/// </summary>
public sealed class EventAndNotificationTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly INotificationService _notifications;
    private readonly BookingNotificationHandlers _handlers;

    public EventAndNotificationTests()
    {
        _notifications = new NotificationService(_h.Db, _h.CurrentUser, _h.Clock);
        _handlers = new BookingNotificationHandlers(
            _notifications, NullLogger<BookingNotificationHandlers>.Instance);
    }

    public void Dispose() => _h.Dispose();

    private async Task<(Guid RenterId, Guid HostId, Guid SpaceId, Guid VehicleId)> SetupAsync(
        decimal recharge = 1000m)
    {
        var renter = await _h.AddUserAsync(UserRole.Renter);
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();

        var space = await _h.AddPublishedSpaceAsync(host.Id, 60m);
        var vehicle = await _h.AddVehicleAsync(renter.Id);
        await _h.Wallets.RechargeAsync(renter.Id, recharge, $"recharge:{renter.Id}");

        _h.CurrentUser.SignIn(renter.Id);

        return (renter.Id, host.Id, space.Id, vehicle.Id);
    }

    [Fact]
    public async Task Booking_a_space_announces_it_with_the_figures_already_settled()
    {
        var (renterId, hostId, spaceId, vehicleId) = await SetupAsync();

        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            spaceId, vehicleId, _h.Clock.UtcNow, 60, "bk-1"));

        var published = _h.Events.Single<BookingCreated>();

        published.BookingId.Should().Be(booking.Id);
        published.RenterId.Should().Be(renterId);
        published.HostId.Should().Be(hostId);
        // Carried on the event rather than left to be re-read: a handler running a second later,
        // or on another machine, should describe what happened then.
        published.HoldAmount.Should().Be(60m);
    }

    [Fact]
    public async Task A_finished_session_announces_what_each_side_got()
    {
        var (_, _, spaceId, vehicleId) = await SetupAsync();

        var start = _h.Clock.UtcNow;
        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            spaceId, vehicleId, start, 60, "bk-1"));
        await _h.Bookings.StartSessionAsync(booking.Id, DetectionMethod.AppConfirmed, start);
        _h.Clock.Advance(TimeSpan.FromMinutes(60));
        await _h.Bookings.EndSessionAsync(booking.Id, DetectionMethod.AppConfirmed, _h.Clock.UtcNow);

        _h.Events.Any<SessionStarted>().Should().BeTrue();

        var ended = _h.Events.Single<SessionEnded>();
        ended.BilledMinutes.Should().Be(60);
        ended.TotalCharged.Should().Be(60m);
        ended.HostCredited.Should().Be(54m);
        ended.Shortfall.Should().Be(0m);
    }

    [Fact]
    public async Task An_uncovered_overstay_is_announced_as_a_shortfall()
    {
        var (_, _, spaceId, vehicleId) = await SetupAsync(recharge: 100m);

        var start = _h.Clock.UtcNow;
        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            spaceId, vehicleId, start, 60, "bk-1"));
        await _h.Bookings.StartSessionAsync(booking.Id, DetectionMethod.AppConfirmed, start);
        _h.Clock.Advance(TimeSpan.FromMinutes(180));
        await _h.Bookings.EndSessionAsync(booking.Id, DetectionMethod.AppConfirmed, _h.Clock.UtcNow);

        _h.Events.Single<SessionEnded>().Shortfall.Should().BeGreaterThan(0m);
    }

    [Fact]
    public async Task Cancelling_announces_the_fee_and_the_refund()
    {
        var (_, _, spaceId, vehicleId) = await SetupAsync();

        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            spaceId, vehicleId, _h.Clock.UtcNow, 120, "bk-1"));

        await _h.Bookings.CancelBookingAsync(booking.Id);

        var cancelled = _h.Events.Single<BookingCancelled>();
        cancelled.Fee.Should().Be(60m);
        cancelled.Refund.Should().Be(60m);
    }

    [Fact]
    public async Task Both_parties_are_told_when_a_space_is_booked()
    {
        var (renterId, hostId, _, _) = await SetupAsync();

        await _handlers.HandleAsync(new BookingCreated(
            Guid.NewGuid(), renterId, hostId, Guid.NewGuid(),
            _h.Clock.UtcNow, _h.Clock.UtcNow.AddHours(1), 60m));

        _h.CurrentUser.SignIn(renterId);
        (await _notifications.GetMineAsync(false, 10)).Should().ContainSingle();

        // The host needs telling too — they may have to move their own car out of the way.
        _h.CurrentUser.SignIn(hostId);
        (await _notifications.GetMineAsync(false, 10)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_shortfall_is_reported_plainly_rather_than_as_a_receipt()
    {
        var (renterId, hostId, _, _) = await SetupAsync();

        await _handlers.HandleAsync(new SessionEnded(
            Guid.NewGuid(), renterId, hostId, 180, 100m, 0m, 90m, Shortfall: 40m));

        _h.CurrentUser.SignIn(renterId);
        var mine = await _notifications.GetMineAsync(false, 10);

        var message = mine.Single();
        message.Kind.Should().Be("session.violation");
        message.Title.Should().Be("Payment incomplete");
        message.Body.Should().Contain("restricted");
    }

    [Fact]
    public async Task An_overstay_tells_the_renter_while_they_can_still_act()
    {
        // The entire reason the meter runs during the session rather than at checkout.
        var (renterId, _, _, _) = await SetupAsync();

        await _handlers.HandleAsync(new OverstayCharged(
            Guid.NewGuid(), renterId, 30, Charged: 30m, Shortfall: 0m));

        _h.CurrentUser.SignIn(renterId);
        var message = (await _notifications.GetMineAsync(false, 10)).Single();

        message.Kind.Should().Be("overstay.charged");
        message.Body.Should().Contain("30 extra minutes");
    }

    [Fact]
    public async Task An_overstay_that_could_not_be_covered_says_how_to_avoid_a_violation()
    {
        var (renterId, _, _, _) = await SetupAsync();

        await _handlers.HandleAsync(new OverstayCharged(
            Guid.NewGuid(), renterId, 30, Charged: 10m, Shortfall: 20m));

        _h.CurrentUser.SignIn(renterId);
        var message = (await _notifications.GetMineAsync(false, 10)).Single();

        message.Kind.Should().Be("overstay.short");
        message.Body.Should().Contain("Add credits");
    }

    [Fact]
    public async Task Both_sides_hear_a_dispute_decision()
    {
        // A dispute settled where only the winner is told is how the same complaint comes back.
        var (renterId, hostId, _, _) = await SetupAsync();

        await _handlers.HandleAsync(new DisputeResolved(
            Guid.NewGuid(), Guid.NewGuid(), renterId, hostId,
            "Resolved", "Host was at fault.", RefundToRenter: 50m));

        foreach (var userId in new[] { renterId, hostId })
        {
            _h.CurrentUser.SignIn(userId);
            var message = (await _notifications.GetMineAsync(false, 10)).Single();
            message.Kind.Should().Be("dispute.resolved");
            message.Body.Should().Contain("Host was at fault.");
        }
    }

    [Fact]
    public async Task Notifications_are_only_ever_the_reader_s_own()
    {
        var (renterId, hostId, _, _) = await SetupAsync();

        await _handlers.HandleAsync(new SessionStarted(
            Guid.NewGuid(), renterId, hostId, _h.Clock.UtcNow, _h.Clock.UtcNow.AddHours(1), "AppConfirmed"));

        _h.CurrentUser.SignIn(hostId);
        (await _notifications.GetMineAsync(false, 10))
            .Should().BeEmpty("only the renter is told their session started");
    }

    [Fact]
    public async Task Reading_one_clears_it_from_the_unread_count()
    {
        var (renterId, hostId, _, _) = await SetupAsync();

        await _handlers.HandleAsync(new BookingCreated(
            Guid.NewGuid(), renterId, hostId, Guid.NewGuid(),
            _h.Clock.UtcNow, _h.Clock.UtcNow.AddHours(1), 60m));

        _h.CurrentUser.SignIn(renterId);
        (await _notifications.GetUnreadCountAsync()).Should().Be(1);

        var message = (await _notifications.GetMineAsync(true, 10)).Single();
        await _notifications.MarkReadAsync(message.Id);

        (await _notifications.GetUnreadCountAsync()).Should().Be(0);
        (await _notifications.GetMineAsync(true, 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task Nobody_can_mark_someone_else_s_notification_read()
    {
        var (renterId, hostId, _, _) = await SetupAsync();

        await _handlers.HandleAsync(new SessionStarted(
            Guid.NewGuid(), renterId, hostId, _h.Clock.UtcNow, _h.Clock.UtcNow.AddHours(1), "AppConfirmed"));

        _h.CurrentUser.SignIn(renterId);
        var message = (await _notifications.GetMineAsync(false, 10)).Single();

        _h.CurrentUser.SignIn(hostId);
        var act = () => _notifications.MarkReadAsync(message.Id);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Money_in_a_notification_carries_a_real_rupee_sign()
    {
        // Written as an escape rather than the character itself, on purpose. If this file were
        // ever misread the same way the handler's source could be, a literal here would be
        // corrupted identically and the test would cheerfully agree with the bug. ₹ is ASCII
        // on disk and cannot be.
        //
        // What this guards is the whole path from source to stored notification staying UTF-8. A
        // database that could not hold this character has already caused one outage here.
        var (renterId, hostId, _, _) = await SetupAsync();

        await _handlers.HandleAsync(new BookingCreated(
            Guid.NewGuid(), renterId, hostId, Guid.NewGuid(),
            _h.Clock.UtcNow, _h.Clock.UtcNow.AddHours(1), 60m));

        _h.CurrentUser.SignIn(renterId);
        var message = (await _notifications.GetMineAsync(false, 10)).Single();

        message.Body.Should().StartWith("₹");
        message.Body.Should().NotContain("â", "that is UTF-8 read as a single-byte codepage");
    }

}
