using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Pricing;
using ParkNest.Domain.Common;
using ParkNest.Domain.Listings;

namespace ParkNest.Application.Listings;

public interface IListingService
{
    Task<ParkingSpace> CreateDraftAsync(CreateListingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Validates the price against every supported vehicle type's band, then goes live.</summary>
    Task<ParkingSpace> PublishAsync(Guid spaceId, CancellationToken cancellationToken = default);

    Task<ParkingSpace> SetStatusAsync(Guid spaceId, SpaceStatus status, CancellationToken cancellationToken = default);
}

/// <summary>The host is always the authenticated caller — see <see cref="CreateBookingRequest"/>
/// for the same reasoning about not accepting a caller-supplied user id.</summary>
public sealed record CreateListingRequest(
    string Title,
    string AddressLine,
    string City,
    string? Zone,
    double Latitude,
    double Longitude,
    decimal PricePerHour,
    IReadOnlyList<VehicleType> SupportedVehicleTypes,
    IReadOnlyList<AvailabilityWindowRequest>? AvailabilityWindows = null,
    string TimeZoneId = "Asia/Kolkata");

/// <param name="EndTime">
/// Earlier than <paramref name="StartTime"/> means the window runs overnight (22:00–06:00).
/// Equal to it means a full 24 hours, which is how a 24/7 basement or society lot is expressed —
/// a zero-length window is represented by having no window at all.
/// </param>
public sealed record AvailabilityWindowRequest(DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime);

public sealed class ListingService : IListingService
{
    private readonly IParkNestDbContext _db;
    private readonly IPricingService _pricing;
    private readonly IClock _clock;
    private readonly ICurrentUser _currentUser;

    public ListingService(IParkNestDbContext db, IPricingService pricing, IClock clock, ICurrentUser currentUser)
    {
        _db = db;
        _pricing = pricing;
        _clock = clock;
        _currentUser = currentUser;
    }

    public async Task<ParkingSpace> CreateDraftAsync(CreateListingRequest request, CancellationToken cancellationToken = default)
    {
        if (request.SupportedVehicleTypes.Count == 0)
        {
            throw new DomainException("A listing must support at least one vehicle type.");
        }

        var hostId = _currentUser.RequireUserId();

        var host = await _db.Users.FirstOrDefaultAsync(u => u.Id == hostId, cancellationToken)
                   ?? throw new DomainException($"User {hostId} does not exist.");

        if (!host.IsHost)
        {
            throw new DomainException("Only a host can create a listing.");
        }

        var space = new ParkingSpace
        {
            HostId = hostId,
            Title = request.Title,
            AddressLine = request.AddressLine,
            City = request.City,
            Zone = request.Zone,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            PricePerHour = Money.Round(request.PricePerHour),
            TimeZoneId = request.TimeZoneId,
            Status = SpaceStatus.Draft,
            CreatedAt = _clock.UtcNow
        };

        foreach (var window in request.AvailabilityWindows ?? Array.Empty<AvailabilityWindowRequest>())
        {
            space.AvailabilityWindows.Add(new AvailabilityWindow
            {
                ParkingSpaceId = space.Id,
                DayOfWeek = window.DayOfWeek,
                StartTime = window.StartTime,
                EndTime = window.EndTime
            });
        }

        foreach (var vehicleType in request.SupportedVehicleTypes.Distinct())
        {
            space.SupportedVehicleTypes.Add(new SpaceVehicleSupport
            {
                ParkingSpaceId = space.Id,
                VehicleType = vehicleType
            });
        }

        _db.ParkingSpaces.Add(space);
        await _db.SaveChangesAsync(cancellationToken);

        return space;
    }

    public async Task<ParkingSpace> PublishAsync(Guid spaceId, CancellationToken cancellationToken = default)
    {
        var space = await LoadAsync(spaceId, cancellationToken);

        // A listing with no hours can never be booked, so publishing it would just be a dead
        // pin on the map for renters to tap.
        if (space.AvailabilityWindows.Count == 0)
        {
            throw new DomainException("Set at least one availability window before publishing.");
        }

        // Every vehicle type the space accepts has its own band; the price must clear all of them.
        foreach (var support in space.SupportedVehicleTypes)
        {
            await _pricing.ValidateListingPriceAsync(
                space.City, space.Zone, support.VehicleType, space.PricePerHour, cancellationToken);
        }

        space.Status = SpaceStatus.Published;
        await _db.SaveChangesAsync(cancellationToken);

        return space;
    }

    public async Task<ParkingSpace> SetStatusAsync(Guid spaceId, SpaceStatus status, CancellationToken cancellationToken = default)
    {
        if (status == SpaceStatus.Published)
        {
            return await PublishAsync(spaceId, cancellationToken);
        }

        var space = await LoadAsync(spaceId, cancellationToken);
        space.Status = status;
        await _db.SaveChangesAsync(cancellationToken);

        return space;
    }

    /// <summary>
    /// Loads a space the caller is allowed to change. Without the ownership check any host could
    /// publish, pause or delist a competitor's listing.
    /// </summary>
    private async Task<ParkingSpace> LoadAsync(Guid spaceId, CancellationToken cancellationToken)
    {
        var space = await _db.ParkingSpaces
            .Include(s => s.SupportedVehicleTypes)
            .Include(s => s.AvailabilityWindows)
            .FirstOrDefaultAsync(s => s.Id == spaceId, cancellationToken)
            ?? throw new DomainException($"Parking space {spaceId} does not exist.");

        _currentUser.RequireSelfOrAdmin(space.HostId);
        return space;
    }
}
