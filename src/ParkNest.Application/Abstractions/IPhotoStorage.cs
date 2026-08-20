namespace ParkNest.Application.Abstractions;

/// <summary>
/// Where listing photos live.
///
/// An interface because the answer differs by deployment and none of the calling code should care:
/// a directory on disk locally, object storage in production. The same reasoning as the payment
/// gateway (ADR 0006) — reproduce the shape that a real provider will need, so switching is a
/// configuration change at the edge rather than a rewrite through the middle.
/// </summary>
public interface IPhotoStorage
{
    /// <summary>Whether photos can be stored at all. False when no provider is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Stores the bytes and returns the URL they will be served from. The caller has already
    /// decided the content is an image it is willing to keep.
    /// </summary>
    Task<string> SaveAsync(Stream content, string extension, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a stored photo. Missing is not an error: the row and the blob can drift, and a
    /// delete that refuses to finish because the file already went leaves the row orphaned forever.
    /// </summary>
    Task DeleteAsync(string url, CancellationToken cancellationToken = default);
}
