using ParkNest.Application.Abstractions;

namespace ParkNest.UnitTests;

/// <summary>
/// Keeps what would have been pushed, and can be told to fail or to disown a token.
///
/// The two behaviours worth simulating are the ones with consequences: a provider that throws
/// must not take a notification down with it, and a token the provider disowns must actually be
/// pruned — otherwise the registration table grows a tail of dead devices that every later send
/// pays for.
/// </summary>
public sealed class RecordingPushSender : IPushSender
{
    private readonly List<(IReadOnlyCollection<string> Tokens, PushMessage Message)> _sent = new();

    public IReadOnlyList<(IReadOnlyCollection<string> Tokens, PushMessage Message)> Sent => _sent;

    /// <summary>Tokens to report as no longer valid, whatever they were sent.</summary>
    public HashSet<string> Dead { get; } = new();

    /// <summary>When set, every send throws — a Firebase outage, or credentials gone stale.</summary>
    public bool Throws { get; set; }

    public Task<IReadOnlyCollection<string>> SendAsync(
        IReadOnlyCollection<string> tokens,
        PushMessage message,
        CancellationToken cancellationToken = default)
    {
        if (Throws)
        {
            throw new InvalidOperationException("Firebase is unreachable.");
        }

        _sent.Add((tokens, message));

        return Task.FromResult<IReadOnlyCollection<string>>(
            tokens.Where(Dead.Contains).ToList());
    }
}
