using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using StackExchange.Redis;

namespace ParkNest.Infrastructure.Caching;

/// <summary>Registered when <c>Cache:Provider</c> is "None". Every read misses, every write drops.</summary>
public sealed class DisabledSearchCache : ISearchCache
{
    public bool IsEnabled => false;

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task SetAsync(string key, string value, TimeSpan timeToLive, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Caches within this process.
///
/// Worth having rather than being a test double: a single-instance deployment loses nothing by it,
/// because there is no second instance for a shared cache to share with. What it cannot do is
/// survive a restart or stay consistent across instances, and an invalidation on one node leaves
/// the others serving stale results — which is precisely the point at which Redis is the answer.
/// </summary>
public sealed class MemorySearchCache : ISearchCache
{
    private readonly IMemoryCache _cache;

    public MemorySearchCache(IMemoryCache cache) => _cache = cache;

    public bool IsEnabled => true;

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_cache.TryGetValue<string>(key, out var value) ? value : null);

    public Task SetAsync(string key, string value, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        // Size is counted in entries against the limit set at registration, so a busy search page
        // cannot grow this without bound. Absolute rather than sliding: a popular origin should
        // still go back to the database on schedule, not stay warm forever on its own popularity.
        _cache.Set(key, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = timeToLive,
            Size = 1
        });

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _cache.Remove(key);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Redis, shared across instances (PRD §15, Phase 2).
///
/// Every operation is wrapped: a cache is an optimisation, and an optimisation that can take the
/// search endpoint down with it when Redis restarts is a liability. A failed read is a miss, a
/// failed write is forgotten, and the request goes to PostGIS exactly as it would with caching
/// switched off.
/// </summary>
public sealed class RedisSearchCache : ISearchCache, IDisposable
{
    private readonly Lazy<ConnectionMultiplexer?> _connection;
    private readonly ILogger<RedisSearchCache> _logger;

    public RedisSearchCache(IOptions<CacheOptions> options, ILogger<RedisSearchCache> logger)
    {
        _logger = logger;

        // Lazy, and tolerant of a Redis that is not there yet. Connecting eagerly in the
        // constructor would make a cold Redis a failed startup, and this service is not important
        // enough to hold the API down.
        _connection = new Lazy<ConnectionMultiplexer?>(() =>
        {
            try
            {
                var configuration = ConfigurationOptions.Parse(options.Value.ConnectionString);
                configuration.AbortOnConnectFail = false;
                return ConnectionMultiplexer.Connect(configuration);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not connect to Redis; geo-search will run uncached.");
                return null;
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsEnabled => true;

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var database = Database();
        if (database is null)
        {
            return null;
        }

        try
        {
            var value = await database.StringGetAsync(key);
            return value.IsNullOrEmpty ? null : value.ToString();
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis read failed for {Key}; treating it as a miss.", key);
            return null;
        }
    }

    public async Task SetAsync(string key, string value, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        var database = Database();
        if (database is null)
        {
            return;
        }

        try
        {
            await database.StringSetAsync(key, value, timeToLive);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis write failed for {Key}; the result is simply not cached.", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var database = Database();
        if (database is null)
        {
            return;
        }

        try
        {
            await database.KeyDeleteAsync(key);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis delete failed for {Key}.", key);
        }
    }

    private IDatabase? Database()
    {
        var connection = _connection.Value;
        return connection is { IsConnected: true } ? connection.GetDatabase() : null;
    }

    public void Dispose()
    {
        if (_connection.IsValueCreated)
        {
            _connection.Value?.Dispose();
        }
    }
}
