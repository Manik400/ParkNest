namespace ParkNest.Application.Abstractions;

/// <summary>
/// Delivering a message to a handset that is not currently looking at the app.
///
/// Deliberately dumb: it is given tokens and a message and reports which tokens the platform says
/// are dead. It does not decide who to tell or in what words — that is the notification module's
/// job, and the push carries the same title and body as the row that was stored, so the two can
/// never say different things about the same event.
/// </summary>
public interface IPushSender
{
    /// <summary>
    /// Sends to every token given.
    ///
    /// Returns the tokens the provider rejected as no longer valid — an uninstalled app, a
    /// restored backup, a token the platform rotated. The caller deletes those: a registration
    /// table nobody prunes grows a tail of dead devices that every future send pays for.
    ///
    /// Failures other than a dead token do not throw. A push is best-effort by nature, and the
    /// stored notification has already been written by the time this is called.
    /// </summary>
    Task<IReadOnlyCollection<string>> SendAsync(
        IReadOnlyCollection<string> tokens,
        PushMessage message,
        CancellationToken cancellationToken = default);
}

/// <param name="Kind">The stable notification kind, so the app can route the tap.</param>
/// <param name="SubjectId">What it is about — a booking, for everything sent today.</param>
public sealed record PushMessage(string Title, string Body, string Kind, Guid? SubjectId);
