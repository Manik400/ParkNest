using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;

namespace ParkNest.Infrastructure.Storage;

/// <summary>
/// Photos on the API's own disk, served back as static files.
///
/// Good enough for a single node and for every development machine, and deliberately not pretending
/// to be more: two API instances do not share a directory, so this is the thing to replace first
/// when the deployment stops being one box. What it does buy is that the whole upload path — the
/// sniffing, the caps, the URLs, the delete ordering — is exercised for real with no account and
/// no network, and swapping in object storage changes only this class.
/// </summary>
public sealed class LocalDiskPhotoStorage : IPhotoStorage
{
    private readonly string _root;
    private readonly string _publicPath;
    private readonly ILogger<LocalDiskPhotoStorage> _logger;

    public LocalDiskPhotoStorage(
        IOptions<StorageOptions> options,
        string contentRoot,
        ILogger<LocalDiskPhotoStorage> logger)
    {
        var settings = options.Value;

        _root = Path.IsPathRooted(settings.LocalRoot)
            ? settings.LocalRoot
            : Path.Combine(contentRoot, settings.LocalRoot);

        _publicPath = '/' + settings.PublicPath.Trim('/');
        _logger = logger;

        Directory.CreateDirectory(_root);
    }

    public bool IsConfigured => true;

    public async Task<string> SaveAsync(
        Stream content,
        string extension,
        CancellationToken cancellationToken = default)
    {
        // A fresh identifier, never the uploaded filename. A caller-supplied name is a path
        // traversal waiting to happen and leaks whatever the host called the file on their phone.
        var name = $"{Guid.NewGuid():N}{extension}";
        var path = Path.Combine(_root, name);

        await using (var file = File.Create(path))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        return $"{_publicPath}/{name}";
    }

    public Task DeleteAsync(string url, CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName(url);

        // Reduced to a bare filename before it touches the filesystem, so a stored value that
        // somehow contains a path cannot reach outside the media directory.
        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.CompletedTask;
        }

        var path = Path.Combine(_root, name);

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            // The row is already gone by the time this runs. A file we could not remove costs
            // disk; throwing here would fail a delete the user has been told succeeded.
            _logger.LogWarning(ex, "Could not delete photo file {Path}.", path);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Stands in when no provider is configured, so the API still starts and the endpoints answer with
/// a clear "not configured" rather than failing dependency injection at boot.
/// </summary>
public sealed class UnconfiguredPhotoStorage : IPhotoStorage
{
    public bool IsConfigured => false;

    public Task<string> SaveAsync(Stream content, string extension, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No photo storage is configured.");

    public Task DeleteAsync(string url, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
