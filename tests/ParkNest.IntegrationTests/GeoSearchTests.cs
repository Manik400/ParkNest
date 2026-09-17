using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Listings;
using ParkNest.Domain.Common;
using ParkNest.Domain.Listings;
using ParkNest.Infrastructure.Persistence;

namespace ParkNest.IntegrationTests;

/// <summary>
/// Geo-search, against a real PostGIS. Everything here depends on the generated <c>geog</c>
/// column being populated from Latitude/Longitude by the database — nothing in the application
/// writes it — so a migration that stopped doing that would fail these and nothing else.
/// </summary>
[Collection(PostgisCollection.Name)]
public sealed class GeoSearchTests : IAsyncLifetime
{
    // Bengaluru. Distances between the fixtures below were chosen to straddle a 2km radius.
    private const double OriginLat = 12.9716;
    private const double OriginLng = 77.5946;

    private readonly PostgisFixture _fixture;
    private readonly List<Guid> _created = new();

    public GeoSearchTests(PostgisFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Removes only what this test wrote. The database is shared and possibly not empty, so
    /// truncating tables would be both destructive and a source of cross-test interference.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (!PostgisFixture.Available || _created.Count == 0)
        {
            return;
        }

        var db = _fixture.Db;
        var spaces = await db.ParkingSpaces.Where(s => _created.Contains(s.Id)).ToListAsync();
        db.ParkingSpaces.RemoveRange(spaces);
        await db.SaveChangesAsync();
    }

    private ISpaceSearchService Search => new PostgresSpaceSearchService(_fixture.Db);

    /// <param name="metresNorth">Offset from the origin. One degree of latitude is ~111,320 m.</param>
    private async Task<ParkingSpace> AddSpaceAsync(
        double metresNorth,
        SpaceStatus status = SpaceStatus.Published,
        decimal pricePerHour = 60m,
        VehicleType vehicleType = VehicleType.FourWheeler)
    {
        var space = new ParkingSpace
        {
            HostId = Guid.NewGuid(),
            Title = $"Space {Guid.NewGuid():N}"[..20],
            AddressLine = "Test address",
            City = "Bengaluru",
            Latitude = OriginLat + metresNorth / 111_320d,
            Longitude = OriginLng,
            PricePerHour = pricePerHour,
            Status = status
        };

        space.SupportedVehicleTypes.Add(new SpaceVehicleSupport
        {
            ParkingSpaceId = space.Id,
            VehicleType = vehicleType
        });

        _fixture.Db.ParkingSpaces.Add(space);
        await _fixture.Db.SaveChangesAsync();
        _created.Add(space.Id);

        return space;
    }

    private static NearbySearchQuery Query(
        int radiusMetres = 2000,
        VehicleType? vehicleType = null,
        decimal? maxPrice = null) =>
        new(OriginLat, OriginLng, radiusMetres, vehicleType, maxPrice);

    [RequiresPostgisFact]
    public async Task A_space_inside_the_radius_is_found_and_one_outside_is_not()
    {
        var near = await AddSpaceAsync(metresNorth: 500);
        var far = await AddSpaceAsync(metresNorth: 5_000);

        var results = await Search.SearchNearbyAsync(Query(radiusMetres: 2000));

        results.Select(r => r.Id).Should().Contain(near.Id).And.NotContain(far.Id);
    }

    [RequiresPostgisFact]
    public async Task Results_come_back_nearest_first()
    {
        var farther = await AddSpaceAsync(metresNorth: 1_500);
        var nearer = await AddSpaceAsync(metresNorth: 200);

        var results = await Search.SearchNearbyAsync(Query());
        var ours = results.Where(r => _created.Contains(r.Id)).ToList();

        ours.Should().HaveCount(2);
        ours[0].Id.Should().Be(nearer.Id);
        ours[1].Id.Should().Be(farther.Id);
    }

    [RequiresPostgisFact]
    public async Task The_reported_distance_is_real_metres()
    {
        await AddSpaceAsync(metresNorth: 1_000);

        var results = await Search.SearchNearbyAsync(Query());
        var found = results.Single(r => _created.Contains(r.Id));

        // Geodesic distance on the spheroid, not a flat approximation, so a tolerance rather than
        // an exact figure — but a wrong SRID or a lat/lng swap would be out by kilometres.
        found.DistanceMetres.Should().BeApproximately(1_000d, 20d);
    }

    [RequiresPostgisFact]
    public async Task A_draft_listing_is_invisible_to_search()
    {
        // Publishing is what puts a space on the map. A draft is a host's work in progress and
        // must not be bookable by a stranger who happened to search the right patch of city.
        await AddSpaceAsync(metresNorth: 300, status: SpaceStatus.Draft);
        await AddSpaceAsync(metresNorth: 400, status: SpaceStatus.Paused);
        var published = await AddSpaceAsync(metresNorth: 500);

        var results = await Search.SearchNearbyAsync(Query());
        var ours = results.Where(r => _created.Contains(r.Id)).ToList();

        ours.Should().ContainSingle().Which.Id.Should().Be(published.Id);
    }

    [RequiresPostgisFact]
    public async Task The_price_ceiling_filters_out_what_the_renter_cannot_afford()
    {
        var cheap = await AddSpaceAsync(metresNorth: 300, pricePerHour: 40m);
        await AddSpaceAsync(metresNorth: 400, pricePerHour: 120m);

        var results = await Search.SearchNearbyAsync(Query(maxPrice: 50m));
        var ours = results.Where(r => _created.Contains(r.Id)).ToList();

        ours.Should().ContainSingle().Which.Id.Should().Be(cheap.Id);
    }

    [RequiresPostgisFact]
    public async Task A_space_that_does_not_take_the_vehicle_is_filtered_out()
    {
        var scooterBay = await AddSpaceAsync(metresNorth: 300, vehicleType: VehicleType.TwoWheeler);
        await AddSpaceAsync(metresNorth: 400, vehicleType: VehicleType.FourWheeler);

        var results = await Search.SearchNearbyAsync(Query(vehicleType: VehicleType.TwoWheeler));
        var ours = results.Where(r => _created.Contains(r.Id)).ToList();

        ours.Should().ContainSingle().Which.Id.Should().Be(scooterBay.Id);
    }

    [RequiresPostgisFact]
    public async Task Moving_a_space_moves_it_on_the_map()
    {
        // The geog column is generated, so this is really asking whether the database recomputes
        // it on update. If it did not, a corrected address would keep matching the old location.
        var space = await AddSpaceAsync(metresNorth: 200);

        space.Latitude = OriginLat + 10_000d / 111_320d;
        await _fixture.Db.SaveChangesAsync();

        var results = await Search.SearchNearbyAsync(Query(radiusMetres: 2000));

        results.Select(r => r.Id).Should().NotContain(space.Id);
    }
}
