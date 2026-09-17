namespace ParkNest.Domain.Listings;

/// <summary>
/// Works out whether a requested window falls entirely inside a space's opening hours.
///
/// Pure functions over local <see cref="DateTime"/> values — the caller converts to the space's
/// local time first. Keeping it free of time-zone lookups and database access makes the edge cases
/// (overnight windows, multi-day bookings, gaps between windows) directly unit-testable.
/// </summary>
public static class AvailabilityCalculator
{
    /// <summary>
    /// True when every moment of <c>[localStart, localEnd)</c> is inside some availability window.
    /// An empty window set means the host published no hours, which is treated as "never open"
    /// rather than "always open" — failing closed is the safer default for someone's driveway.
    /// </summary>
    public static bool IsCovered(
        IReadOnlyCollection<AvailabilityWindow> windows,
        DateTime localStart,
        DateTime localEnd)
    {
        if (localEnd <= localStart)
        {
            return false;
        }

        if (windows.Count == 0)
        {
            return false;
        }

        var intervals = Expand(windows, localStart, localEnd);
        if (intervals.Count == 0)
        {
            return false;
        }

        // Walk the merged intervals, advancing a cursor. If the cursor ever fails to move past a
        // gap, some part of the booking is outside opening hours.
        var cursor = localStart;

        foreach (var (from, to) in Merge(intervals))
        {
            if (from > cursor)
            {
                return false;
            }

            if (to > cursor)
            {
                cursor = to;
            }

            if (cursor >= localEnd)
            {
                return true;
            }
        }

        return cursor >= localEnd;
    }

    /// <summary>
    /// Projects recurring weekly windows onto concrete dates covering the requested range.
    /// </summary>
    private static List<(DateTime From, DateTime To)> Expand(
        IReadOnlyCollection<AvailabilityWindow> windows,
        DateTime localStart,
        DateTime localEnd)
    {
        var result = new List<(DateTime, DateTime)>();

        // Start a day early so an overnight window opened the previous evening is included.
        var date = localStart.Date.AddDays(-1);
        var lastDate = localEnd.Date;

        while (date <= lastDate)
        {
            foreach (var window in windows)
            {
                if (window.DayOfWeek != date.DayOfWeek)
                {
                    continue;
                }

                var from = date + window.StartTime.ToTimeSpan();

                // EndTime <= StartTime means the window runs past midnight into the next day
                // (e.g. a basement open 22:00–06:00).
                var to = window.EndTime > window.StartTime
                    ? date + window.EndTime.ToTimeSpan()
                    : date.AddDays(1) + window.EndTime.ToTimeSpan();

                if (to > localStart && from < localEnd)
                {
                    result.Add((from, to));
                }
            }

            date = date.AddDays(1);
        }

        return result;
    }

    /// <summary>Sorts and coalesces overlapping or touching intervals into a minimal set.</summary>
    private static List<(DateTime From, DateTime To)> Merge(List<(DateTime From, DateTime To)> intervals)
    {
        intervals.Sort((a, b) => a.From.CompareTo(b.From));

        var merged = new List<(DateTime From, DateTime To)>();

        foreach (var interval in intervals)
        {
            if (merged.Count > 0 && interval.From <= merged[^1].To)
            {
                // Adjacent windows (17:00–20:00 followed by 20:00–23:00) form one continuous
                // opening, so a booking may legitimately span the seam.
                if (interval.To > merged[^1].To)
                {
                    merged[^1] = (merged[^1].From, interval.To);
                }
            }
            else
            {
                merged.Add(interval);
            }
        }

        return merged;
    }
}
