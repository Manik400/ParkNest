using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Abstractions;
using ParkNest.Domain.Common;
using ParkNest.Domain.Notifications;

namespace ParkNest.Application.Notifications;

public interface INotificationService
{
    Task<IReadOnlyList<NotificationView>> GetMineAsync(bool onlyUnread, int limit, CancellationToken cancellationToken = default);

    Task<int> GetUnreadCountAsync(CancellationToken cancellationToken = default);

    Task MarkReadAsync(Guid notificationId, CancellationToken cancellationToken = default);

    Task MarkAllReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a message for a user. Called by event handlers rather than by request handlers —
    /// nothing a user does creates their own notification.
    /// </summary>
    Task<NotificationView> CreateAsync(CreateNotification request, CancellationToken cancellationToken = default);
}

public sealed record CreateNotification(Guid UserId, string Kind, string Title, string Body, Guid? SubjectId = null);

public sealed record NotificationView(
    Guid Id,
    string Kind,
    string Title,
    string Body,
    Guid? SubjectId,
    bool IsRead,
    DateTimeOffset CreatedAt);

public sealed class NotificationService : INotificationService
{
    private readonly IParkNestDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public NotificationService(IParkNestDbContext db, ICurrentUser currentUser, IClock clock)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<IReadOnlyList<NotificationView>> GetMineAsync(
        bool onlyUnread,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();

        var query = _db.Notifications.AsNoTracking().Where(n => n.UserId == userId);

        if (onlyUnread)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        return await query
            .OrderByDescending(n => n.CreatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(n => new NotificationView(
                n.Id, n.Kind, n.Title, n.Body, n.SubjectId, n.ReadAt != null, n.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetUnreadCountAsync(CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();

        return await _db.Notifications
            .CountAsync(n => n.UserId == userId && n.ReadAt == null, cancellationToken);
    }

    public async Task MarkReadAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();

        var notification = await _db.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId, cancellationToken)
            ?? throw new DomainException("That notification does not exist.");

        // Scoped to the owner rather than checked and reported: whether someone else's
        // notification exists is not something a caller needs to learn.
        if (notification.UserId != userId)
        {
            throw new ForbiddenException("That notification is not yours.");
        }

        notification.ReadAt ??= _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();
        var now = _clock.UtcNow;

        var unread = await _db.Notifications
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .ToListAsync(cancellationToken);

        foreach (var notification in unread)
        {
            notification.ReadAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<NotificationView> CreateAsync(
        CreateNotification request,
        CancellationToken cancellationToken = default)
    {
        var notification = new Notification
        {
            UserId = request.UserId,
            Kind = request.Kind,
            Title = request.Title,
            Body = request.Body,
            SubjectId = request.SubjectId,
            CreatedAt = _clock.UtcNow,
        };

        _db.Notifications.Add(notification);
        await _db.SaveChangesAsync(cancellationToken);

        return new NotificationView(
            notification.Id,
            notification.Kind,
            notification.Title,
            notification.Body,
            notification.SubjectId,
            false,
            notification.CreatedAt);
    }
}
