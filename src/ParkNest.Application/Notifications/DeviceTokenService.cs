using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Abstractions;
using ParkNest.Domain.Common;
using ParkNest.Domain.Notifications;

namespace ParkNest.Application.Notifications;

public interface IDeviceTokenService
{
    /// <summary>
    /// Records where to push for this install. Idempotent — the app registers on every launch,
    /// because the platform rotates tokens whenever it feels like it.
    /// </summary>
    Task RegisterAsync(RegisterDevice request, CancellationToken cancellationToken = default);

    /// <summary>Stops pushing to this install. Called on sign-out, from the device signing out.</summary>
    Task UnregisterAsync(string token, CancellationToken cancellationToken = default);
}

public sealed record RegisterDevice(string Token, string Platform);

public sealed class DeviceTokenService : IDeviceTokenService
{
    private static readonly string[] KnownPlatforms = ["android", "ios", "web"];

    private readonly IParkNestDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public DeviceTokenService(IParkNestDbContext db, ICurrentUser currentUser, IClock clock)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task RegisterAsync(RegisterDevice request, CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            throw new DomainException("A device token is required.");
        }

        var platform = request.Platform?.Trim().ToLowerInvariant() ?? string.Empty;

        if (!KnownPlatforms.Contains(platform))
        {
            throw new DomainException($"Platform must be one of: {string.Join(", ", KnownPlatforms)}.");
        }

        var token = request.Token.Trim();
        var existing = await _db.DeviceTokens.FirstOrDefaultAsync(d => d.Token == token, cancellationToken);

        if (existing is null)
        {
            _db.DeviceTokens.Add(new DeviceToken
            {
                UserId = userId,
                Token = token,
                Platform = platform,
                CreatedAt = _clock.UtcNow,
                LastSeenAt = _clock.UtcNow,
            });

            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        // Moved rather than duplicated. A handset someone borrowed to sign in must stop receiving
        // the previous owner's bookings the moment its new owner registers it — the token
        // identifies the install, and only one account can be signed in on it at a time.
        existing.UserId = userId;
        existing.Platform = platform;
        existing.LastSeenAt = _clock.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UnregisterAsync(string token, CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();

        var existing = await _db.DeviceTokens
            .FirstOrDefaultAsync(d => d.Token == token && d.UserId == userId, cancellationToken);

        // Silent when there is nothing to remove. Signing out twice, or from a device that never
        // registered, is not a failure the user can act on — and reporting it would tell a caller
        // whether a token they guessed belongs to someone else.
        if (existing is null)
        {
            return;
        }

        _db.DeviceTokens.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
