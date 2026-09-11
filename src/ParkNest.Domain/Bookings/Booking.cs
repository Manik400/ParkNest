using ParkNest.Domain.Common;

namespace ParkNest.Domain.Bookings;

public class Booking
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ParkingSpaceId { get; set; }
    public Guid RenterId { get; set; }
    public Guid HostId { get; set; }
    public Guid VehicleId { get; set; }

    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset ExpectedEndTime { get; set; }
    public DateTimeOffset? ActualStartTime { get; set; }
    public DateTimeOffset? ActualEndTime { get; set; }

    /// <summary>Rate snapshotted at booking time, so a later listing edit can't re-price a live session.</summary>
    public decimal RatePerHour { get; set; }

    /// <summary>Overstay multiplier snapshotted from the city band at booking time.</summary>
    public decimal OverstayMultiplier { get; set; } = 1.0m;

    public int BookedMinutes { get; set; }

    /// <summary>Minutes actually billed after rounding up to the billing increment.</summary>
    public int? BilledMinutes { get; set; }

    /// <summary>Credits reserved up front (booked duration × rate).</summary>
    public decimal HoldAmount { get; set; }

    /// <summary>Extra credits pulled in to cover time beyond the booked window.</summary>
    public decimal OverstayAmount { get; set; }

    /// <summary>Total moved to the host and the platform at settlement.</summary>
    public decimal SettledAmount { get; set; }

    /// <summary>Commission taken out of <see cref="SettledAmount"/> before crediting the host.</summary>
    public decimal PlatformFee { get; set; }

    /// <summary>Amount the renter owed at settlement but could not cover. Drives <see cref="BookingStatus.InViolation"/>.</summary>
    public decimal ShortfallAmount { get; set; }

    /// <summary>
    /// The session that was still parked here when this booking's slot came due, if one was.
    ///
    /// Set once, by the overstay meter, and never cleared. It is a record of a promise as much as
    /// a state: once a renter has been told their slot could not be delivered and that cancelling
    /// is free, the previous car driving off two minutes later must not quietly withdraw that.
    /// </summary>
    public Guid? BlockedByBookingId { get; set; }

    /// <summary>When the block was first noticed. Null whenever <see cref="BlockedByBookingId"/> is.</summary>
    public DateTimeOffset? BlockedAt { get; set; }

    public BookingStatus Status { get; set; } = BookingStatus.Held;
    public DetectionMethod? StartDetectionMethod { get; set; }
    public DetectionMethod? EndDetectionMethod { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsSettled => Status is BookingStatus.Completed or BookingStatus.InViolation;

    /// <summary>The slot could not be delivered, because the previous car was still in it.</summary>
    public bool WasBlocked => BlockedByBookingId is not null;
}
