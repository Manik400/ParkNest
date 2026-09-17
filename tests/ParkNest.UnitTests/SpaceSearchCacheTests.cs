using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ParkNest.Application.Listings;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;
using ParkNest.Infrastructure.Caching;

namespace ParkNest.UnitTests;

/// <summary>
/// The cache in front of geo-search. Run against the real in-process implementation rather than a
/// stub, so key building, expiry and the generation counter are all genuinely exercised — a cache
/// tested only through a fake tends to pass while serving the wrong results.
/// </summary>
public sealed class SpaceSearchCacheTests : IDisposable
{
    private readonly TestClock _clock = new(TestHarness.Origin);
    private readonly CountingSearchService _inner = new();
    private readonly MemoryCache _memory = new(new MemoryCacheOptions { SizeLimit = 1000 });
    private readonly CacheOptions _options = new()
    {
        Provider = "Memory",
        KeyPrefix = "test",
        SearchTtlSeconds = 60,
        OriginPrecision = 4
    };

    public void Dispose() => _memory.Dispose();

    private CachingSpaceSearchService Build() => new(
        _inner,
        new MemorySearchCache(_memory),
        _clock,
        Options.Create(_options),
        NullLogger<CachingSpaceSearchService>.Instance);

    private static NearbySearchQuery Query(
        double lat = 12.9716,
        double lng = 77.5946,
        int radius = 2000,
        VehicleType? vehicle = null,
        decimal? maxPrice = null,
        int limit = 50) =>
        new(lat, lng, radius, vehicle, maxPrice, limit);

    [Fact]
    public async Task The_second_identical_search_does_not_reach_the_database()
    {
        var search = Build();

        var first = await search.SearchNearbyAsync(Query());
        var second = await search.SearchNearbyAsync(Query());

        _inner.Calls.Should().Be(1);
        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task Nearly_identical_coordinates_share_one_entry()
    {
        var search = Build();

        await search.SearchNearbyAsync(Query(lat: 12.97161234));
        await search.SearchNearbyAsync(Query(lat: 12.97161999));

        // Without rounding the origin, a phone's GPS jitter mints a new key on every tap and the
        // cache never hits at all. Four decimal places is roughly eleven metres.
        _inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Coordinates_further_apart_than_the_grid_do_not_share_an_entry()
    {
        var search = Build();

        await search.SearchNearbyAsync(Query(lat: 12.9716));
        await search.SearchNearbyAsync(Query(lat: 12.9750));

        _inner.Calls.Should().Be(2);
    }

    [Theory]
    [InlineData(3000, null, null, 50)]
    [InlineData(2000, VehicleType.TwoWheeler, null, 50)]
    [InlineData(2000, null, 40, 50)]
    [InlineData(2000, null, null, 10)]
    public async Task Every_filter_is_part_of_the_key(
        int radius,
        VehicleType? vehicle,
        int? maxPrice,
        int limit)
    {
        var search = Build();

        await search.SearchNearbyAsync(Query());
        await search.SearchNearbyAsync(Query(
            radius: radius,
            vehicle: vehicle,
            maxPrice: maxPrice,
            limit: limit));

        // A filter left out of the key is the classic way a cache becomes a correctness bug —
        // a scooter search served the car results, or a 500m search served the 3km ones.
        _inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Invalidating_retires_every_cached_search_at_once()
    {
        var search = Build();

        await search.SearchNearbyAsync(Query(lat: 12.9716));
        await search.SearchNearbyAsync(Query(lat: 12.9750));
        _inner.Calls.Should().Be(2);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await search.InvalidateAsync();

        // A new listing changes the answer for every origin within its radius, and those keys were
        // minted from coordinates nobody recorded. Both must go.
        await search.SearchNearbyAsync(Query(lat: 12.9716));
        await search.SearchNearbyAsync(Query(lat: 12.9750));
        _inner.Calls.Should().Be(4);
    }

    [Fact]
    public async Task An_entry_is_not_served_past_its_lifetime()
    {
        _options.SearchTtlSeconds = 1;
        var search = Build();

        await search.SearchNearbyAsync(Query());
        _inner.Calls.Should().Be(1);

        // MemoryCache expires on wall-clock time, not the injected clock, so this waits for real.
        // Kept to a single second precisely because it is a real wait.
        await Task.Delay(TimeSpan.FromMilliseconds(1300));

        await search.SearchNearbyAsync(Query());
        _inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task With_caching_off_every_search_reaches_the_database()
    {
        var search = new CachingSpaceSearchService(
            _inner,
            new DisabledSearchCache(),
            _clock,
            Options.Create(new CacheOptions { Provider = "None" }),
            NullLogger<CachingSpaceSearchService>.Instance);

        await search.SearchNearbyAsync(Query());
        await search.SearchNearbyAsync(Query());

        _inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task The_cached_result_carries_the_same_values_as_the_query_that_filled_it()
    {
        var search = Build();

        var live = await search.SearchNearbyAsync(Query());
        var cached = await search.SearchNearbyAsync(Query());

        // Round-tripped through JSON, so a field that does not survive serialisation shows up here
        // rather than as an empty title on somebody's map.
        cached.Should().HaveCount(live.Count);
        cached[0].Id.Should().Be(live[0].Id);
        cached[0].Title.Should().Be(live[0].Title);
        cached[0].PricePerHour.Should().Be(live[0].PricePerHour);
        cached[0].DistanceMetres.Should().Be(live[0].DistanceMetres);
        cached[0].Latitude.Should().Be(live[0].Latitude);
    }

    /// <summary>Stands in for PostGIS, and counts how often it was actually asked.</summary>
    private sealed class CountingSearchService : ISpaceSearchService
    {
        private static readonly Guid SpaceId = Guid.NewGuid();

        public int Calls { get; private set; }

        public Task<IReadOnlyList<NearbySpace>> SearchNearbyAsync(
            NearbySearchQuery query,
            CancellationToken cancellationToken = default)
        {
            Calls++;

            IReadOnlyList<NearbySpace> results =
            [
                new NearbySpace(SpaceId, "Indiranagar driveway", "12th Main", "Bengaluru", 12.9716, 77.5946, 45.50m, 120.5, null)
            ];

            return Task.FromResult(results);
        }
    }
}
