namespace ParkNest.Domain.Notifications;

/// <summary>
/// Where to push to reach one installation of the app.
///
/// Per device rather than per user: a person routinely carries the app on a phone and leaves it
/// installed on an old one, and a message about an overstay that reached only the drawer is worse
/// than none. The token belongs to the install, not the account — it survives sign-out and is
/// re-registered on the next sign-in, which is why a token seen for a different user is *moved*
/// rather than duplicated. Someone signing in on a friend's handset must not keep receiving their
/// bookings there afterwards.
/// </summary>
public class DeviceToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>The FCM registration token. Long, opaque, and rotated by the platform at will.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>"android", "ios" or "web". Recorded for diagnosis, not for routing.</summary>
    public string Platform { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Refreshed every time the app registers. A token nothing has re-registered in months is a
    /// dead install, and the thing to prune first when the send fan-out gets expensive.
    /// </summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}
