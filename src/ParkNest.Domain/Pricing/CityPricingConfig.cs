using ParkNest.Domain.Common;

namespace ParkNest.Domain.Pricing;

/// <summary>
/// Admin-managed price band per city / zone / vehicle type. Data-driven by design (PRD §9):
/// changing a band must never require a code deploy.
/// </summary>
public class CityPricingConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string City { get; set; } = string.Empty;

    /// <summary>Null means "applies to the whole city" — a zone-specific row wins over it.</summary>
    public string? Zone { get; set; }

    public VehicleType VehicleType { get; set; }

    public decimal MinPricePerHour { get; set; }
    public decimal MaxPricePerHour { get; set; }

    /// <summary>Applied to the base rate for minutes beyond the booked window. 1.0 = no premium.</summary>
    public decimal OverstayMultiplier { get; set; } = 1.0m;

    public bool IsActive { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool Allows(decimal pricePerHour) => pricePerHour >= MinPricePerHour && pricePerHour <= MaxPricePerHour;
}
