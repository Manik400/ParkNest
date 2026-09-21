namespace ParkNest.Application.Options;

public sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";

    /// <summary>
    /// Off switch. False stops every recorder writing — the endpoints still answer, and the
    /// dashboard still shows whatever was collected before — so a table growing faster than the
    /// free database tier can hold is one environment variable away from stopping, with no deploy.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Email addresses allowed to read the dashboard, on top of holding the admin role.
    ///
    /// Empty means any admin, which is the sensible default for a one-person platform. Naming an
    /// address narrows it further: an operator brought in to clear the identity-check queue then
    /// gets that queue and not the revenue figures. Compared case-insensitively.
    /// </summary>
    public string[] Viewers { get; set; } = Array.Empty<string>();

    /// <summary>
    /// How long a row is kept. Everything older is deleted by the daily sweep.
    ///
    /// A year, because the useful question is "how did this month compare with last" and nothing
    /// asks about a page view from two years ago. Zero or less switches the sweep off, which is a
    /// choice to let the table grow.
    /// </summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>How often the retention sweep runs. It deletes by date, so a missed pass costs nothing.</summary>
    public int RetentionSweepHours { get; set; } = 24;

    /// <summary>
    /// The longest window the dashboard may ask for in one go. The daily series is aggregated in
    /// memory, so this is what bounds how much gets read to answer one request.
    /// </summary>
    public int MaxWindowDays { get; set; } = 180;
}
