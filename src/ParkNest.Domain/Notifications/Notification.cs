namespace ParkNest.Domain.Notifications;

/// <summary>
/// Something the platform told a user.
///
/// Stored rather than only pushed, because a push is a best-effort delivery to a device that may
/// be off, out of signal, or not installed yet. "Your overstay is being billed" is not a message
/// anyone should miss because their phone was in a basement — the socket and the push are how it
/// arrives quickly, this row is how it arrives at all.
/// </summary>
public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>Stable key for the kind of message, so a client can localise or style it.</summary>
    public string Kind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    /// <summary>The booking or payout it concerns, so a client can deep-link to it.</summary>
    public Guid? SubjectId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }

    public bool IsRead => ReadAt.HasValue;
}
