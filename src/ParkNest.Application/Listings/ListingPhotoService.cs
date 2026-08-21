using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Common;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;
using ParkNest.Domain.Listings;

namespace ParkNest.Application.Listings;

public interface IListingPhotoService
{
    /// <summary>Adds a photo to a listing the caller hosts. Returns the stored photo.</summary>
    Task<ListingPhotoView> AddAsync(Guid spaceId, PhotoUpload upload, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ListingPhotoView>> ListAsync(Guid spaceId, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid spaceId, Guid photoId, CancellationToken cancellationToken = default);
}

public sealed record ListingPhotoView(Guid Id, string Url, int SortOrder);

public sealed class ListingPhotoService : IListingPhotoService
{
    private readonly IParkNestDbContext _db;
    private readonly IPhotoStorage _storage;
    private readonly ICurrentUser _currentUser;
    private readonly StorageOptions _options;

    public ListingPhotoService(
        IParkNestDbContext db,
        IPhotoStorage storage,
        ICurrentUser currentUser,
        IOptions<StorageOptions> options)
    {
        _db = db;
        _storage = storage;
        _currentUser = currentUser;
        _options = options.Value;
    }

    public async Task<ListingPhotoView> AddAsync(
        Guid spaceId,
        PhotoUpload upload,
        CancellationToken cancellationToken = default)
    {
        if (!_storage.IsConfigured)
        {
            throw new DomainException("Photo storage is not configured on this deployment.");
        }

        var space = await LoadOwnedSpaceAsync(spaceId, cancellationToken);

        if (upload.Length <= 0)
        {
            throw new DomainException("That file is empty.");
        }

        if (upload.Length > _options.MaxPhotoBytes)
        {
            throw new DomainException(
                $"Photos must be under {_options.MaxPhotoBytes / (1024 * 1024)} MB.");
        }

        var existing = await _db.SpacePhotos
            .Where(p => p.ParkingSpaceId == spaceId)
            .OrderBy(p => p.SortOrder)
            .ToListAsync(cancellationToken);

        if (existing.Count >= _options.MaxPhotosPerListing)
        {
            throw new DomainException(
                $"A listing can hold {_options.MaxPhotosPerListing} photos. Remove one first.");
        }

        var extension = await ImageContent.SniffExtensionAsync(upload.Content, cancellationToken);
        var url = await _storage.SaveAsync(upload.Content, extension, cancellationToken);

        var photo = new SpacePhoto
        {
            ParkingSpaceId = space.Id,
            Url = url,
            // Appended rather than inserted: the first photo a host uploads is the one they think
            // represents the space, and later ones should not displace it.
            SortOrder = existing.Count == 0 ? 0 : existing[^1].SortOrder + 1,
        };

        _db.SpacePhotos.Add(photo);
        await _db.SaveChangesAsync(cancellationToken);

        return new ListingPhotoView(photo.Id, photo.Url, photo.SortOrder);
    }

    public async Task<IReadOnlyList<ListingPhotoView>> ListAsync(
        Guid spaceId,
        CancellationToken cancellationToken = default)
    {
        var space = await _db.ParkingSpaces
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == spaceId, cancellationToken)
            ?? throw new DomainException($"Listing {spaceId} does not exist.");

        // Published listings are public, so their photos are too — that is the point of them.
        // A draft is the host's work in progress and stays theirs until they publish.
        if (space.Status != SpaceStatus.Published)
        {
            _currentUser.RequireSelfOrAdmin(space.HostId);
        }

        return await _db.SpacePhotos
            .AsNoTracking()
            .Where(p => p.ParkingSpaceId == spaceId)
            .OrderBy(p => p.SortOrder)
            .Select(p => new ListingPhotoView(p.Id, p.Url, p.SortOrder))
            .ToListAsync(cancellationToken);
    }

    public async Task RemoveAsync(Guid spaceId, Guid photoId, CancellationToken cancellationToken = default)
    {
        await LoadOwnedSpaceAsync(spaceId, cancellationToken);

        var photo = await _db.SpacePhotos
            .FirstOrDefaultAsync(p => p.Id == photoId && p.ParkingSpaceId == spaceId, cancellationToken)
            ?? throw new DomainException("That photo is not on this listing.");

        _db.SpacePhotos.Remove(photo);
        await _db.SaveChangesAsync(cancellationToken);

        // The row goes first and the blob after. The other order can delete a file and then fail
        // to save, leaving a listing pointing at nothing; this way the worst case is an orphaned
        // file, which costs disk rather than showing a broken image to a renter.
        await _storage.DeleteAsync(photo.Url, cancellationToken);
    }

    private async Task<ParkingSpace> LoadOwnedSpaceAsync(Guid spaceId, CancellationToken cancellationToken)
    {
        var space = await _db.ParkingSpaces.FirstOrDefaultAsync(s => s.Id == spaceId, cancellationToken)
                    ?? throw new DomainException($"Listing {spaceId} does not exist.");

        _currentUser.RequireSelfOrAdmin(space.HostId);

        return space;
    }
}
