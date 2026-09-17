namespace ParkNest.Domain.Common;

/// <summary>
/// The caller is asking for something faster than the platform is willing to serve it. Maps to
/// HTTP 429.
///
/// Deliberately not a <see cref="DomainException"/>: nothing about the request is wrong, and a 400
/// would tell a client to stop retrying when the correct advice is to retry later. The
/// <see cref="RetryAfter"/> is surfaced so the caller does not have to guess.
/// </summary>
public sealed class TooManyRequestsException : Exception
{
    public TooManyRequestsException(string message, TimeSpan retryAfter) : base(message) =>
        RetryAfter = retryAfter;

    public TimeSpan RetryAfter { get; }
}
