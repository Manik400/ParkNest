namespace ParkNest.Application.Abstractions;

/// <summary>
/// A string key/value store with an expiry, and nothing more.
///
/// Deliberately this small. The application does not want a cache library's opinions in it, and
/// keeping the surface to four methods means the no-op implementation is honest rather than a
/// stub full of <c>NotSupportedException</c>. Redis and an in-process dictionary both satisfy it.
/// </summary>
public interface ISearchCache
{
    /// <summary>False when no provider is configured, in which case the other methods do nothing.</summary>
    bool IsEnabled { get; }

    /// <summary>The stored value, or null when it is absent, expired, or the cache is unreachable.</summary>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string value, TimeSpan timeToLive, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes one key. Missing is not an error, for the same reason it is not one when deleting a
    /// photo: the cache and the truth are allowed to drift, and that is the direction we want.
    /// </summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
