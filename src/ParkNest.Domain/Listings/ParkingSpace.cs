using ParkNest.Domain.Common;

namespace ParkNest.Domain.Listings;

public class ParkingSpace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HostId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string AddressLine { get; set; } = string.Empty;

    /// <summary>Must match a <c>CityPricingConfig.City</c> row — it selects the price band.</summary>
    public string City { get; set; } = string.Empty;

    /// <summary>Optional zone tier within the city (e.g. "cbd", "suburb") for finer price bands.</summary>
    public string? Zone { get; set; }

    /// <summary>
    /// IANA time zone the availability windows are expressed in. Windows are wall-clock ("open
    /// 09:00–17:00"), so without this a multi-city rollout would silently interpret every host's
    /// hours in the server's zone.
    /// </summary>
    public string TimeZoneId { get; set; } = "Asia/Kolkata";

    public double Latitude { get; set; }
    public double Longitude { get; set; }

    /// <summary>Bit flags are avoided deliberately; a space may support several vehicle types.</summary>
    public ICollection<SpaceVehicleSupport> SupportedVehicleTypes { get; set; } = new List<SpaceVehicleSupport>();

    public ICollection<AvailabilityWindow> AvailabilityWindows { get; set; } = new List<AvailabilityWindow>();
    public ICollection<SpacePhoto> Photos { get; set; } = new List<SpacePhoto>();

    /// <summary>Validated against the city band at publish time (PRD §9).</summary>
    public decimal PricePerHour { get; set; }

    /// <summary>
    /// Secret behind the QR sticker at the space, for Tier 2 check-in. Null until the host asks
    /// for a code, so an existing listing keeps working on Tier 1 without anything to migrate.
    ///
    /// Not a password: anyone standing at the space can read it off the wall, and that is the
    /// point — it corroborates presence. On its own it proves only that someone has been there
    /// once, which is why it is paired with a geofence check rather than trusted alone.
    /// </summary>
    public string? CheckInToken { get; set; }

    public SpaceStatus Status { get; set; } = SpaceStatus.Draft;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool Supports(VehicleType type) => SupportedVehicleTypes.Any(v => v.VehicleType == type);
}

public class SpaceVehicleSupport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParkingSpaceId { get; set; }
    public VehicleType VehicleType { get; set; }
}

public class SpacePhoto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParkingSpaceId { get; set; }
    public string Url { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}
