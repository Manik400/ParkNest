using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ParkNest.Application.Notifications;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

/// <summary>
/// Where to push, and what happens when pushing does not work.
///
/// The property being defended is that push is the fast half of a delivery and never the whole
/// of it: the stored notification is written first and stands on its own, so nothing about
/// Firebase — an outage, stale credentials, a device that no longer exists — may cost a user the
/// message itself.
/// </summary>
public sealed class PushNotificationTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly RecordingPushSender _push = new();
    private readonly NotificationService _notifications;
    private readonly DeviceTokenService _devices;

    public PushNotificationTests()
    {
        _notifications = new NotificationService(
            _h.Db, _h.CurrentUser, _h.Clock, _push, NullLogger<NotificationService>.Instance);

        _devices = new DeviceTokenService(_h.Db, _h.CurrentUser, _h.Clock);
    }

    public void Dispose() => _h.Dispose();

    private async Task<Guid> SignedInUserAsync()
    {
        var user = await _h.AddUserAsync(UserRole.Renter);
        _h.CurrentUser.SignIn(user.Id);
        return user.Id;
    }

    [Fact]
    public async Task Registering_the_same_device_twice_leaves_one_row()
    {
        var userId = await SignedInUserAsync();

        // The app registers on every launch, because the platform rotates the token whenever it
        // likes and the app cannot tell whether this one is new.
        await _devices.RegisterAsync(new RegisterDevice("token-abc", "android"));
        await _devices.RegisterAsync(new RegisterDevice("token-abc", "android"));

        var devices = await _h.Db.DeviceTokens.Where(d => d.UserId == userId).ToListAsync();

        devices.Should().ContainSingle();
        devices.Single().LastSeenAt.Should().Be(_h.Clock.UtcNow);
    }

    [Fact]
    public async Task A_device_registered_by_someone_else_moves_rather_than_duplicating()
    {
        var first = await SignedInUserAsync();
        await _devices.RegisterAsync(new RegisterDevice("shared-handset", "android"));

        var second = await SignedInUserAsync();
        await _devices.RegisterAsync(new RegisterDevice("shared-handset", "android"));

        var devices = await _h.Db.DeviceTokens.ToListAsync();

        // Someone who signed in on a friend's phone must stop receiving their bookings there the
        // moment the friend signs back in. Two rows would push every message to both accounts.
        devices.Should().ContainSingle();
        devices.Single().UserId.Should().Be(second);
        devices.Single().UserId.Should().NotBe(first);
    }

    [Fact]
    public async Task Unregistering_only_touches_your_own_device()
    {
        var owner = await SignedInUserAsync();
        await _devices.RegisterAsync(new RegisterDevice("owner-handset", "ios"));

        await SignedInUserAsync();

        // A token guessed or scraped by another account is not theirs to remove — and the silence
        // is deliberate, since an error would confirm the token exists.
        await _devices.UnregisterAsync("owner-handset");

        var devices = await _h.Db.DeviceTokens.ToListAsync();
        devices.Should().ContainSingle().Which.UserId.Should().Be(owner);
    }

    [Fact]
    public async Task An_unknown_platform_is_refused()
    {
        await SignedInUserAsync();

        var act = () => _devices.RegisterAsync(new RegisterDevice("token-abc", "blackberry"));

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task A_notification_pushes_to_every_device_the_user_registered()
    {
        var userId = await SignedInUserAsync();
        await _devices.RegisterAsync(new RegisterDevice("phone", "android"));
        await _devices.RegisterAsync(new RegisterDevice("tablet", "android"));

        await _notifications.CreateAsync(new CreateNotification(
            userId, "overstay.charged", "You are past your slot", "₹40.00 taken.", Guid.NewGuid()));

        var sent = _push.Sent.Should().ContainSingle().Subject;

        sent.Tokens.Should().BeEquivalentTo(["phone", "tablet"]);
        // The push says exactly what the stored row says. Two texts for one event is how a user
        // ends up with a banner and a list entry that disagree.
        sent.Message.Title.Should().Be("You are past your slot");
        sent.Message.Body.Should().Be("₹40.00 taken.");
        sent.Message.Kind.Should().Be("overstay.charged");
    }

    [Fact]
    public async Task A_token_the_provider_disowns_is_pruned()
    {
        var userId = await SignedInUserAsync();
        await _devices.RegisterAsync(new RegisterDevice("live-phone", "android"));
        await _devices.RegisterAsync(new RegisterDevice("uninstalled", "android"));
        _push.Dead.Add("uninstalled");

        await _notifications.CreateAsync(new CreateNotification(
            userId, "session.ended", "Session finished", "₹60.00 charged.", null));

        var devices = await _h.Db.DeviceTokens.Select(d => d.Token).ToListAsync();

        // Nothing else would ever notice: an uninstalled app never unregisters, and a table full
        // of dead tokens is paid for by every send that follows.
        devices.Should().BeEquivalentTo(["live-phone"]);
    }

    [Fact]
    public async Task A_failing_provider_does_not_cost_the_user_the_message()
    {
        var userId = await SignedInUserAsync();
        await _devices.RegisterAsync(new RegisterDevice("phone", "android"));
        _push.Throws = true;

        var view = await _notifications.CreateAsync(new CreateNotification(
            userId, "session.violation", "Payment incomplete", "₹120.00 could not be collected.", null));

        // The row is the delivery that matters. This one is the message telling a renter their
        // account is restricted; losing it because Firebase was down would be indefensible.
        view.Title.Should().Be("Payment incomplete");
        (await _h.Db.Notifications.CountAsync(n => n.UserId == userId)).Should().Be(1);
    }

    [Fact]
    public async Task Nothing_is_sent_to_a_user_with_no_devices()
    {
        var userId = await SignedInUserAsync();

        await _notifications.CreateAsync(new CreateNotification(
            userId, "booking.created", "Space reserved", "₹60.00 held.", null));

        _push.Sent.Should().BeEmpty();
    }
}
