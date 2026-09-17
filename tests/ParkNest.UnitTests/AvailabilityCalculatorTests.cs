using FluentAssertions;
using ParkNest.Domain.Listings;

namespace ParkNest.UnitTests;

/// <summary>
/// Pure coverage logic — the fiddly cases are overnight windows, bookings that span midnight, and
/// gaps between two windows on the same day.
/// </summary>
public sealed class AvailabilityCalculatorTests
{
    private static AvailabilityWindow Window(DayOfWeek day, int fromHour, int toHour) => new()
    {
        DayOfWeek = day,
        StartTime = new TimeOnly(fromHour, 0),
        EndTime = new TimeOnly(toHour, 0)
    };

    // 2026-07-28 is a Tuesday.
    private static DateTime Tuesday(int hour, int minute = 0) => new(2026, 7, 28, hour, minute, 0);

    [Fact]
    public void A_booking_inside_a_window_is_covered() =>
        AvailabilityCalculator
            .IsCovered(new[] { Window(DayOfWeek.Tuesday, 9, 17) }, Tuesday(10), Tuesday(12))
            .Should().BeTrue();

    [Fact]
    public void A_booking_starting_before_opening_is_not_covered() =>
        AvailabilityCalculator
            .IsCovered(new[] { Window(DayOfWeek.Tuesday, 9, 17) }, Tuesday(8), Tuesday(10))
            .Should().BeFalse();

    [Fact]
    public void A_booking_running_past_closing_is_not_covered() =>
        AvailabilityCalculator
            .IsCovered(new[] { Window(DayOfWeek.Tuesday, 9, 17) }, Tuesday(16), Tuesday(18))
            .Should().BeFalse();

    [Fact]
    public void A_booking_exactly_filling_the_window_is_covered() =>
        AvailabilityCalculator
            .IsCovered(new[] { Window(DayOfWeek.Tuesday, 9, 17) }, Tuesday(9), Tuesday(17))
            .Should().BeTrue();

    [Fact]
    public void A_booking_on_a_day_with_no_window_is_not_covered() =>
        AvailabilityCalculator
            .IsCovered(new[] { Window(DayOfWeek.Wednesday, 0, 23) }, Tuesday(10), Tuesday(12))
            .Should().BeFalse();

    [Fact]
    public void No_windows_at_all_means_never_available() =>
        AvailabilityCalculator
            .IsCovered(Array.Empty<AvailabilityWindow>(), Tuesday(10), Tuesday(12))
            .Should().BeFalse();

    [Fact]
    public void A_booking_spanning_a_gap_between_two_windows_is_not_covered()
    {
        var windows = new[] { Window(DayOfWeek.Tuesday, 9, 12), Window(DayOfWeek.Tuesday, 14, 18) };

        AvailabilityCalculator.IsCovered(windows, Tuesday(11), Tuesday(15)).Should().BeFalse();
    }

    [Fact]
    public void Two_adjacent_windows_form_one_continuous_opening()
    {
        var windows = new[] { Window(DayOfWeek.Tuesday, 9, 12), Window(DayOfWeek.Tuesday, 12, 18) };

        AvailabilityCalculator.IsCovered(windows, Tuesday(11), Tuesday(15)).Should().BeTrue();
    }

    [Fact]
    public void An_overnight_window_covers_a_booking_after_midnight()
    {
        // Monday 22:00 → Tuesday 06:00.
        var windows = new[] { Window(DayOfWeek.Monday, 22, 6) };

        AvailabilityCalculator.IsCovered(windows, Tuesday(1), Tuesday(3)).Should().BeTrue();
    }

    [Fact]
    public void An_overnight_window_does_not_cover_the_following_afternoon()
    {
        var windows = new[] { Window(DayOfWeek.Monday, 22, 6) };

        AvailabilityCalculator.IsCovered(windows, Tuesday(14), Tuesday(16)).Should().BeFalse();
    }

    [Fact]
    public void Equal_start_and_end_means_a_full_24_hours()
    {
        var windows = new[] { Window(DayOfWeek.Tuesday, 0, 0) };

        AvailabilityCalculator.IsCovered(windows, Tuesday(3), Tuesday(23)).Should().BeTrue();
    }

    [Fact]
    public void A_booking_crossing_midnight_needs_both_days_open()
    {
        var onlyTuesday = new[] { Window(DayOfWeek.Tuesday, 0, 0) };
        var bothDays = new[] { Window(DayOfWeek.Tuesday, 0, 0), Window(DayOfWeek.Wednesday, 0, 0) };

        var start = Tuesday(23);
        var end = new DateTime(2026, 7, 29, 2, 0, 0);

        AvailabilityCalculator.IsCovered(onlyTuesday, start, end).Should().BeFalse();
        AvailabilityCalculator.IsCovered(bothDays, start, end).Should().BeTrue();
    }

    [Fact]
    public void A_zero_length_or_inverted_request_is_never_covered()
    {
        var windows = new[] { Window(DayOfWeek.Tuesday, 0, 0) };

        AvailabilityCalculator.IsCovered(windows, Tuesday(10), Tuesday(10)).Should().BeFalse();
        AvailabilityCalculator.IsCovered(windows, Tuesday(12), Tuesday(10)).Should().BeFalse();
    }
}
