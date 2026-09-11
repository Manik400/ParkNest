using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Notifications;

namespace ParkNest.Api.Controllers;

/// <summary>
/// The durable half of telling someone something. The socket and the push are how a message
/// arrives quickly; this is how it arrives at all.
/// </summary>
[ApiController]
[Route("api/notifications")]
public sealed class NotificationsController : ControllerBase
{
    private readonly INotificationService _notifications;

    public NotificationsController(INotificationService notifications) => _notifications = notifications;

    [HttpGet("me")]
    public async Task<ActionResult<IReadOnlyList<NotificationView>>> Mine(
        [FromQuery] bool onlyUnread = false,
        [FromQuery] int limit = 30,
        CancellationToken cancellationToken = default) =>
        Ok(await _notifications.GetMineAsync(onlyUnread, limit, cancellationToken));

    /// <summary>Just the number, for a badge. Cheaper than fetching a list to count it.</summary>
    [HttpGet("me/unread-count")]
    public async Task<ActionResult<int>> UnreadCount(CancellationToken cancellationToken) =>
        Ok(await _notifications.GetUnreadCountAsync(cancellationToken));

    [HttpPost("{notificationId:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid notificationId, CancellationToken cancellationToken)
    {
        await _notifications.MarkReadAsync(notificationId, cancellationToken);
        return NoContent();
    }

    [HttpPost("me/read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        await _notifications.MarkAllReadAsync(cancellationToken);
        return NoContent();
    }
}
