using Microsoft.Extensions.Logging;
using ParkNest.Application.Abstractions;

namespace ParkNest.Infrastructure.Notifications;

/// <summary>
/// What runs when no Firebase project is configured: nothing, loudly enough to debug.
///
/// A registered no-op rather than an unregistered interface. Leaving <see cref="IPushSender"/>
/// unresolved would fail DI validation and take the API down over a feature the platform works
/// without — every message is still stored, and still pushed down the socket to anything
/// connected.
/// </summary>
public sealed class DisabledPushSender : IPushSender
{
    private readonly ILogger<DisabledPushSender> _logger;

    public DisabledPushSender(ILogger<DisabledPushSender> logger) => _logger = logger;

    public Task<IReadOnlyCollection<string>> SendAsync(
        IReadOnlyCollection<string> tokens,
        PushMessage message,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Push is not configured; {Kind} for {DeviceCount} device(s) was stored but not pushed.",
            message.Kind, tokens.Count);

        // No token is dead, because none was tried. Reporting them as dead would prune a
        // registration table that is doing nothing wrong.
        return Task.FromResult<IReadOnlyCollection<string>>([]);
    }
}
