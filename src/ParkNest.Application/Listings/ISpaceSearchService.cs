using ParkNest.Domain.Common;

namespace ParkNest.Application.Listings;

public interface ISpaceSearchService
{
    /// <summary>
    /// Spaces within <paramref name="radiusMetres"/> of a point, nearest first. Backed by a
    /// PostGIS GiST index; target latency is under 300ms (PRD §15).
    /// </summary>
    Task<IReadOnlyList<NearbySpace>> SearchNearbyAsync(NearbySearchQuery query, CancellationToken cancellationToken = default);
}

public sealed record NearbySearchQuery(
    double Latitude,
    double Longitude,
    int RadiusMetres = 2000,
    VehicleType? VehicleType = null,
    decimal? MaxPricePerHour = null,
    int Limit = 50);

/// <summary>
/// One search hit. <paramref name="PhotoUrl"/> is the listing's first photo, or null when the host
/// has not uploaded one — the results page leads with the photo, so fetching it per card would
/// be one round trip per result.
/// </summary>
public sealed record NearbySpace(
    Guid Id,
    string Title,
    string AddressLine,
    string City,
    double Latitude,
    double Longitude,
    decimal PricePerHour,
    double DistanceMetres,
    string? PhotoUrl);
