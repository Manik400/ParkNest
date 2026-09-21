using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Domain.Analytics;
using ParkNest.Domain.Common;

namespace ParkNest.Application.Analytics;

// --- What the dashboard reads ------------------------------------------------------------

/// <param name="Visits">Sessions started — someone opened the site. The hit counter.</param>
/// <param name="Visitors">Distinct browsers. Lower than visits, and the honest headline number.</param>
public sealed record AnalyticsTotals(
    long Visits,
    long PageViews,
    long Visitors,
    long Searches,
    long SignIns,
    long SignUps,
    long Bookings,
    decimal BookingValue,
    long PaymentAttempts,
    long PaymentsSucceeded,
    long PaymentsFailed,
    decimal PaymentValue);

public sealed record AnalyticsDay(
    DateOnly Date,
    long Visits,
    long PageViews,
    long Visitors,
    long Bookings,
    long PaymentAttempts,
    long PaymentsSucceeded);

public sealed record AnalyticsPage(string Path, long Views);

/// <param name="LastSeen">When it last happened, which is how you tell a dead counter from a quiet one.</param>
public sealed record AnalyticsCounter(string Name, long Count, DateTimeOffset LastSeen);

public sealed record AnalyticsRecentEvent(
    string Name,
    DateTimeOffset OccurredAt,
    string Source,
    string? Path,
    decimal? Amount,
    string? Detail,
    bool SignedIn);

/// <param name="AllTimeVisits">Since the counter was switched on, ignoring the selected window.</param>
public sealed record AnalyticsSummary(
    DateTimeOffset From,
    DateTimeOffset To,
    int Days,
    AnalyticsTotals Totals,
    IReadOnlyList<AnalyticsDay> Daily,
    IReadOnlyList<AnalyticsPage> TopPages,
    IReadOnlyList<AnalyticsCounter> Counters,
    IReadOnlyList<AnalyticsRecentEvent> Recent,
    long AllTimeVisits,
    long AllTimePageViews,
    long AllTimeVisitors,
    long AllTimeEvents);

public interface IAnalyticsQueries
{
    /// <summary>
    /// The whole dashboard in one call. Authorises the caller itself — this is the only read path
    /// to the figures, so the check belongs here rather than in whichever controller calls it.
    /// </summary>
    Task<AnalyticsSummary> GetSummaryAsync(int days, CancellationToken cancellationToken = default);
}

public sealed class AnalyticsQueries : IAnalyticsQueries
{
    /// <summary>Rows in the latest-events feed. Enough to see the last few minutes, not a log viewer.</summary>
    private const int RecentLimit = 40;
    private const int TopPagesLimit = 12;

    private readonly IParkNestDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly AnalyticsOptions _options;

    public AnalyticsQueries(
        IParkNestDbContext db,
        ICurrentUser currentUser,
        IClock clock,
        IOptions<AnalyticsOptions> options)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<AnalyticsSummary> GetSummaryAsync(int days, CancellationToken cancellationToken = default)
    {
        RequireViewer();

        days = Math.Clamp(days <= 0 ? 30 : days, 1, Math.Max(1, _options.MaxWindowDays));

        var now = _clock.UtcNow;

        // Whole days, back from today, so "7 days" means this day and the six before it rather
        // than a window that slides through midnight while you are looking at it.
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var from = new DateTimeOffset(today.AddDays(-(days - 1)).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        // Read the window once and aggregate in memory, rather than a query per figure. At a
        // pilot's volume this is one small scan against the (OccurredAt, Name) index, and the day
        // ceiling above is what keeps it small. When it stops fitting, this is the method to push
        // down into SQL — nothing it returns needs to change.
        var rows = await _db.AnalyticsEvents
            .AsNoTracking()
            .Where(e => e.OccurredAt >= from)
            .Select(e => new Row(
                e.Name,
                e.OccurredAt,
                e.VisitorId,
                e.Path,
                e.Amount,
                e.Detail,
                e.Source,
                e.UserId))
            .ToListAsync(cancellationToken);

        var topPages = rows
            .Where(r => r.Name == AnalyticsEventNames.PageView && !string.IsNullOrEmpty(r.Path))
            .GroupBy(r => r.Path!, StringComparer.Ordinal)
            .Select(g => new AnalyticsPage(g.Key, g.LongCount()))
            .OrderByDescending(p => p.Views)
            .ThenBy(p => p.Path, StringComparer.Ordinal)
            .Take(TopPagesLimit)
            .ToList();

        // Every name that fired, not only the ones with a tile of their own. Whatever gets
        // instrumented next appears here without another edit to this class.
        var counters = rows
            .GroupBy(r => r.Name, StringComparer.Ordinal)
            .Select(g => new AnalyticsCounter(g.Key, g.LongCount(), g.Max(r => r.OccurredAt)))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var recent = rows
            .OrderByDescending(r => r.OccurredAt)
            .Take(RecentLimit)
            .Select(r => new AnalyticsRecentEvent(
                r.Name, r.OccurredAt, r.Source, r.Path, r.Amount, r.Detail, r.UserId.HasValue))
            .ToList();

        // The lifetime figures stay in SQL: they span the whole table, which is exactly what must
        // not be pulled into memory.
        var allTimeEvents = await _db.AnalyticsEvents.LongCountAsync(cancellationToken);

        var allTimeVisits = await _db.AnalyticsEvents
            .LongCountAsync(e => e.Name == AnalyticsEventNames.SiteVisit, cancellationToken);

        var allTimePageViews = await _db.AnalyticsEvents
            .LongCountAsync(e => e.Name == AnalyticsEventNames.PageView, cancellationToken);

        var allTimeVisitors = await _db.AnalyticsEvents
            .Where(e => e.VisitorId != null)
            .Select(e => e.VisitorId)
            .Distinct()
            .LongCountAsync(cancellationToken);

        return new AnalyticsSummary(
            from,
            now,
            days,
            Totals(rows),
            Daily(rows, today, days),
            topPages,
            counters,
            recent,
            allTimeVisits,
            allTimePageViews,
            allTimeVisitors,
            allTimeEvents);
    }

    private static AnalyticsTotals Totals(IReadOnlyList<Row> rows) => new(
        Visits: Count(rows, AnalyticsEventNames.SiteVisit),
        PageViews: Count(rows, AnalyticsEventNames.PageView),
        Visitors: Visitors(rows),
        Searches: Count(rows, AnalyticsEventNames.SearchRun),
        SignIns: Count(rows, AnalyticsEventNames.SignedIn),
        SignUps: Count(rows, AnalyticsEventNames.SignedUp),
        Bookings: Count(rows, AnalyticsEventNames.BookingCreated),
        BookingValue: Sum(rows, AnalyticsEventNames.BookingCreated),
        PaymentAttempts: Count(rows, AnalyticsEventNames.PaymentStarted),
        PaymentsSucceeded: Count(rows, AnalyticsEventNames.PaymentSucceeded),
        PaymentsFailed: Count(rows, AnalyticsEventNames.PaymentFailed),
        PaymentValue: Sum(rows, AnalyticsEventNames.PaymentSucceeded));

    /// <summary>
    /// One entry per day in the window, including the days nothing happened — a chart with gaps
    /// where the quiet days were reads as missing data rather than as a quiet day.
    /// </summary>
    private static List<AnalyticsDay> Daily(IReadOnlyList<Row> rows, DateOnly today, int days)
    {
        var byDay = rows
            .GroupBy(r => DateOnly.FromDateTime(r.OccurredAt.UtcDateTime))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Row>)g.ToList());

        var result = new List<AnalyticsDay>(days);

        for (var offset = days - 1; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);

            if (!byDay.TryGetValue(date, out var day))
            {
                result.Add(new AnalyticsDay(date, 0, 0, 0, 0, 0, 0));
                continue;
            }

            result.Add(new AnalyticsDay(
                date,
                Count(day, AnalyticsEventNames.SiteVisit),
                Count(day, AnalyticsEventNames.PageView),
                Visitors(day),
                Count(day, AnalyticsEventNames.BookingCreated),
                Count(day, AnalyticsEventNames.PaymentStarted),
                Count(day, AnalyticsEventNames.PaymentSucceeded)));
        }

        return result;
    }

    private static long Count(IEnumerable<Row> rows, string name) =>
        rows.LongCount(r => r.Name == name);

    private static long Visitors(IEnumerable<Row> rows) =>
        rows.Where(r => !string.IsNullOrEmpty(r.VisitorId))
            .Select(r => r.VisitorId!)
            .Distinct(StringComparer.Ordinal)
            .LongCount();

    private static decimal Sum(IEnumerable<Row> rows, string name) =>
        rows.Where(r => r.Name == name && r.Amount.HasValue).Sum(r => r.Amount!.Value);

    /// <summary>
    /// Admin, and — when the allow-list names anyone — that person specifically.
    ///
    /// Two checks rather than one, because they answer different questions. The role is what the
    /// rest of the console is gated on; the list is what keeps "who signed up and what did they
    /// pay" with the owner even after a second operator is given the admin role to work the
    /// identity-check queue.
    /// </summary>
    private void RequireViewer()
    {
        _currentUser.RequireAdmin();

        if (_options.Viewers.Length == 0)
        {
            return;
        }

        var email = _currentUser.Email;

        var allowed = email is not null && _options.Viewers.Any(
            v => string.Equals(v.Trim(), email, StringComparison.OrdinalIgnoreCase));

        if (!allowed)
        {
            throw new ForbiddenException("These figures are restricted to the platform owner.");
        }
    }

    /// <summary>The columns the summary needs. Projected so a wide row is never materialised.</summary>
    private sealed record Row(
        string Name,
        DateTimeOffset OccurredAt,
        string? VisitorId,
        string? Path,
        decimal? Amount,
        string? Detail,
        string Source,
        Guid? UserId);
}
