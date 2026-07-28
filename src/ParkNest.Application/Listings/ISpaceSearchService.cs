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

public sealed record NearbySpace(
    Guid Id,
    string Title,
    string AddressLine,
    double Latitude,
    double Longitude,
    decimal PricePerHour,
    double DistanceMetres);
