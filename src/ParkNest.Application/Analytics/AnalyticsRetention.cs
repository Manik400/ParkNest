using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;

namespace ParkNest.Application.Analytics;

public interface IAnalyticsRetention
{
    /// <summary>Deletes hits older than the configured window. Returns how many went.</summary>
    Task<int> PruneAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps the tally from outgrowing the database it lives in.
///
/// This is the one table in the system that grows with traffic rather than with business, and on
/// a free Postgres tier that is a real ceiling — a year of page views is worth keeping, five is
/// worth nothing and costs the space the bookings need.
/// </summary>
public sealed class AnalyticsRetention : IAnalyticsRetention
{
    /// <summary>Rows per pass. Small enough that each transaction is short, large enough to finish.</summary>
    private const int BatchSize = 1000;

    private readonly IParkNestDbContext _db;
    private readonly IClock _clock;
    private readonly AnalyticsOptions _options;
    private readonly ILogger<AnalyticsRetention> _logger;

    public AnalyticsRetention(
        IParkNestDbContext db,
        IClock clock,
        IOptions<AnalyticsOptions> options,
        ILogger<AnalyticsRetention> logger)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        if (_options.RetentionDays <= 0)
        {
            return 0;
        }

        var cutoff = _clock.UtcNow.AddDays(-_options.RetentionDays);
        var deleted = 0;

        // In batches, and through the ordinary API rather than a bulk delete: this layer is
        // provider-agnostic on purpose, and a year of hits removed in one statement would hold a
        // transaction open over the table the site is still writing to.
        while (true)
        {
            var batch = await _db.AnalyticsEvents
                .Where(e => e.OccurredAt < cutoff)
                .OrderBy(e => e.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            _db.AnalyticsEvents.RemoveRange(batch);
            await _db.SaveChangesAsync(cancellationToken);
            deleted += batch.Count;
        }

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Pruned {Count} analytics events recorded before {Cutoff}.", deleted, cutoff);
        }

        return deleted;
    }
}
