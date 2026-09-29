using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Domain.Analytics;

namespace ParkNest.Application.Analytics;

/// <summary>What to count, and anything worth knowing alongside it. Every field but the name is optional.</summary>
public sealed record AnalyticsHit(
    string Name,
    Guid? UserId = null,
    string? VisitorId = null,
    string? SessionId = null,
    string? Path = null,
    string? Referrer = null,
    string Source = AnalyticsSources.Server,
    decimal? Amount = null,
    string? Detail = null);

/// <summary>
/// Writes hits to the tally.
///
/// The contract that matters is the one about failure: <see cref="RecordAsync"/> never throws.
/// It is called from the middle of a checkout and from the middle of a booking, and a counter
/// that could fail a payment would be worse than no counter at all.
///
/// It shares the request's <see cref="IParkNestDbContext"/> and saves through it, the same way the
/// notification handlers do, so callers record <i>after</i> their own save — recording first would
/// commit whatever they still had staged.
/// </summary>
public interface IAnalyticsRecorder
{
    Task RecordAsync(AnalyticsHit hit, CancellationToken cancellationToken = default);
}

public sealed class AnalyticsRecorder : IAnalyticsRecorder
{
    /// <summary>
    /// Long enough for the real values and short enough that a stranger cannot use the anonymous
    /// endpoint to store paragraphs. Matched to the column widths, so a long value is truncated
    /// here rather than rejected by the database at save time.
    /// </summary>
    private const int MaxIdLength = 64;
    private const int MaxPathLength = 256;
    private const int MaxDetailLength = 128;

    private readonly IParkNestDbContext _db;
    private readonly IClock _clock;
    private readonly AnalyticsOptions _options;
    private readonly ILogger<AnalyticsRecorder> _logger;

    public AnalyticsRecorder(
        IParkNestDbContext db,
        IClock clock,
        IOptions<AnalyticsOptions> options,
        ILogger<AnalyticsRecorder> logger)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RecordAsync(AnalyticsHit hit, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(hit.Name))
        {
            return;
        }

        try
        {
            _db.AnalyticsEvents.Add(new AnalyticsEvent
            {
                Name = Trim(hit.Name, MaxIdLength)!,
                OccurredAt = _clock.UtcNow,
                UserId = hit.UserId,
                VisitorId = Trim(hit.VisitorId, MaxIdLength),
                SessionId = Trim(hit.SessionId, MaxIdLength),
                Path = Trim(hit.Path, MaxPathLength),
                Referrer = Trim(hit.Referrer, MaxPathLength),
                Source = Trim(hit.Source, 16) ?? AnalyticsSources.Server,
                Amount = hit.Amount,
                Detail = Trim(hit.Detail, MaxDetailLength)
            });

            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallowed, and this is the whole point of the class. The caller is usually a booking
            // or a payment that has already happened; letting a full disk or a dropped connection
            // escape from here would turn a missing row on a dashboard into a failed checkout.
            _logger.LogWarning(ex, "Could not record analytics event {Name}.", hit.Name);
        }
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
