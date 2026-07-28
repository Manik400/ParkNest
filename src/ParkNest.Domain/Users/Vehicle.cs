using ParkNest.Domain.Common;

namespace ParkNest.Domain.Users;

public class Vehicle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Normalised (upper-case, no spaces). Also the key ANPR matches against in Tier 3.</summary>
    public string PlateNumber { get; set; } = string.Empty;

    public VehicleType Type { get; set; }
}
