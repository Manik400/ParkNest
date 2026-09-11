using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;

namespace ParkNest.Application.Listings;

/// <summary>
/// Serves a repeated geo-search from cache instead of PostGIS (PRD §15, Phase 2).
///
/// A decorator rather than caching inside <c>PostgresSpaceSearchService</c>, so the query and the
/// decision to remember its answer stay separable — the integration suite exercises the real SQL
/// by resolving the inner service, and switching the cache off removes this class from the chain
/// entirely rather than leaving a disabled branch inside the hot path.
/// </summary>
public sealed class CachingSpaceSearchService : ISpaceSearchService, ISpaceSearchCacheInvalidator
{
    private readonly ISpaceSearchService _inner;
    private readonly ISearchCache _cache;
    private readonly IClock _clock;
    private readonly CacheOptions _options;
    private readonly ILogger<CachingSpaceSearchService> _logger;

    public CachingSpaceSearchService(
        ISpaceSearchService inner,
        ISearchCache cache,
        IClock clock,
        IOptions<CacheOptions> options,
        ILogger<CachingSpaceSearchService> logger)
    {
        _inner = inner;
        _cache = cache;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NearbySpace>> SearchNearbyAsync(
        NearbySearchQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!_cache.IsEnabled)
        {
            return await _inner.SearchNearbyAsync(query, cancellationToken);
        }

        var generation = await ReadGenerationAsync(cancellationToken);
        var key = BuildKey(query, generation);

        var cached = await _cache.GetAsync(key, cancellationToken);
        if (cached is not null)
        {
            var hit = Deserialize(cached);
            if (hit is not null)
            {
                return hit;
            }

            // Something is in the slot that this version of the code cannot read — an entry left
            // by an older shape of NearbySpace, most likely. Fall through to the database rather
            // than fail the search, and the write below replaces it.
            _logger.LogWarning("Discarding an unreadable geo-search cache entry at {Key}.", key);
        }

        var results = await _inner.SearchNearbyAsync(query, cancellationToken);

        await _cache.SetAsync(
            key,
            JsonSerializer.Serialize(results),
            TimeSpan.FromSeconds(Math.Max(1, _options.SearchTtlSeconds)),
            cancellationToken);

        return results;
    }

    /// <summary>
    /// Retires every cached search at once by moving the generation the keys are built from.
    ///
    /// Targeted invalidation is not available here and pretending otherwise would be worse than
    /// this: a listing that appears, moves, changes price or is unpublished alters the answer for
    /// every origin within its radius, which is an unbounded set of keys that were minted from
    /// coordinates nobody recorded. Bumping a counter makes all of them unreachable in one write,
    /// and the orphans expire on their own within the TTL. Listing edits are rare next to
    /// searches, so the cost lands in the right place.
    /// </summary>
    public async Task InvalidateAsync(CancellationToken cancellationToken = default)
    {
        if (!_cache.IsEnabled)
        {
            return;
        }

        // The generation is a timestamp rather than an incremented counter so that this needs no
        // atomic read-modify-write. Two invalidations racing both write a value nothing has issued
        // keys under yet, so either winning is correct.
        await _cache.SetAsync(
            GenerationKey,
            _clock.UtcNow.UtcTicks.ToString(CultureInfo.InvariantCulture),
            GenerationLifetime,
            cancellationToken);
    }

    private async Task<string> ReadGenerationAsync(CancellationToken cancellationToken)
    {
        var stored = await _cache.GetAsync(GenerationKey, cancellationToken);
        if (!string.IsNullOrEmpty(stored))
        {
            return stored;
        }

        // Absent because nothing has been cached yet, or because the entry expired. Seeding a
        // fresh value costs one round of misses; the alternative — a fixed fallback generation —
        // would resurrect entries written before an invalidation that has since expired.
        var seeded = _clock.UtcNow.UtcTicks.ToString(CultureInfo.InvariantCulture);
        await _cache.SetAsync(GenerationKey, seeded, GenerationLifetime, cancellationToken);
        return seeded;
    }

    private string BuildKey(NearbySearchQuery query, string generation)
    {
        var precision = Math.Clamp(_options.OriginPrecision, 0, 6);
        var lat = Math.Round(query.Latitude, precision).ToString("F" + precision, CultureInfo.InvariantCulture);
        var lng = Math.Round(query.Longitude, precision).ToString("F" + precision, CultureInfo.InvariantCulture);

        // Every field of the query is in the key. A filter left out of it would serve a scooter
        // search the car results, which is the classic way a cache turns into a correctness bug.
        return string.Join(
            ':',
            _options.KeyPrefix,
            "search",
            generation,
            lat,
            lng,
            query.RadiusMetres.ToString(CultureInfo.InvariantCulture),
            query.VehicleType?.ToString() ?? "any",
            query.MaxPricePerHour?.ToString(CultureInfo.InvariantCulture) ?? "any",
            query.Limit.ToString(CultureInfo.InvariantCulture));
    }

    private static IReadOnlyList<NearbySpace>? Deserialize(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<List<NearbySpace>>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string GenerationKey => $"{_options.KeyPrefix}:search:generation";

    /// <summary>
    /// Far longer than any search entry. It only has to outlive them: while the generation stands,
    /// the entries issued under it stay reachable, and once it is gone they are already expired.
    /// </summary>
    private static readonly TimeSpan GenerationLifetime = TimeSpan.FromDays(7);
}

/// <summary>
/// Lets listing writes retire the cached search results they invalidate, without the listing code
/// knowing whether a cache exists.
/// </summary>
public interface ISpaceSearchCacheInvalidator
{
    Task InvalidateAsync(CancellationToken cancellationToken = default);
}

/// <summary>Registered when caching is off, so callers need no null check.</summary>
public sealed class NoOpSpaceSearchCacheInvalidator : ISpaceSearchCacheInvalidator
{
    public Task InvalidateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
