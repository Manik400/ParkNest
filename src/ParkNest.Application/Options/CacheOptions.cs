namespace ParkNest.Application.Options;

/// <summary>
/// The shared cache in front of geo-search (PRD §15, Phase 2).
///
/// Off by default, and that is not a stopgap. Geo-search is one indexed PostGIS query and it is
/// already inside its latency budget on a single city; a cache earns its place when the same few
/// thousand origins are asked for repeatedly across several instances, which is a multi-city
/// problem. Requiring Redis to run the application would be paying that cost years early, in
/// every developer's setup instructions.
/// </summary>
public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>
    /// "Redis" for a shared cache; "Memory" to cache within this process; "None" to disable.
    ///
    /// "Memory" is genuinely useful rather than a placeholder — a single-instance deployment gets
    /// the whole benefit of caching without an extra service, because there is no second instance
    /// for the entries to be shared with. It stops being enough the moment there are two.
    /// </summary>
    public string Provider { get; set; } = "None";

    /// <summary>StackExchange.Redis configuration string, e.g. <c>localhost:6379</c>.</summary>
    public string ConnectionString { get; set; } = "localhost:6379";

    /// <summary>
    /// Namespace for every key, so one Redis can hold more than one environment without staging
    /// answering production's searches.
    /// </summary>
    public string KeyPrefix { get; set; } = "parknest";

    /// <summary>
    /// How long a search result may be served from cache.
    ///
    /// Short on purpose. A listing that was edited or unpublished is invalidated explicitly, so
    /// this is only the ceiling on how stale a result can get through some path nothing thought
    /// to invalidate — and a renter shown a space that is no longer there loses a drive, not a
    /// millisecond.
    /// </summary>
    public int SearchTtlSeconds { get; set; } = 60;

    /// <summary>
    /// Decimal places the search origin is rounded to before it becomes part of the cache key.
    ///
    /// Without this the cache never hits: a phone's GPS moves several metres between two taps and
    /// every reading is a distinct key. Four places is about 11 metres at this latitude, so
    /// neighbouring requests collapse onto one entry. The cost is that the distances in a cached
    /// result are measured from the rounded point rather than the caller's exact one — an error
    /// bounded by the cell, invisible next to a figure displayed as "120 m away", and it is why
    /// this is configuration rather than a constant.
    /// </summary>
    public int OriginPrecision { get; set; } = 4;

    /// <summary>
    /// Entries the in-process cache may hold before it starts evicting.
    ///
    /// A cap rather than a target. Search keys are minted from coordinates, so the key space is
    /// effectively unbounded and an uncapped dictionary of them is a memory leak that takes a week
    /// to show itself. Ignored by the Redis provider, which has its own eviction policy.
    /// </summary>
    public long MemoryEntryLimit { get; set; } = 10_000;

    public bool IsRedis => string.Equals(Provider, "Redis", StringComparison.OrdinalIgnoreCase);

    public bool IsMemory => string.Equals(Provider, "Memory", StringComparison.OrdinalIgnoreCase);

    public bool IsEnabled => IsRedis || IsMemory;
}
