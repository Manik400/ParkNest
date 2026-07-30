namespace ParkNest.Domain.Listings;

/// <summary>
/// A recurring weekly window during which the space can be booked, expressed in the space's
/// local time. Blackout dates are modelled separately as <see cref="AvailabilityBlackout"/>.
/// </summary>
public class AvailabilityWindow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParkingSpaceId { get; set; }

    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }

    /// <summary>
    /// Earlier than <see cref="StartTime"/> means the window runs past midnight; equal to it means
    /// a full 24 hours. See <see cref="AvailabilityCalculator"/>.
    /// </summary>
    public TimeOnly EndTime { get; set; }

    public bool Covers(DayOfWeek day, TimeOnly from, TimeOnly to) =>
        DayOfWeek == day && from >= StartTime && to <= EndTime;
}

public class AvailabilityBlackout
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParkingSpaceId { get; set; }
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public string? Reason { get; set; }
}
