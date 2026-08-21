namespace ParkNest.Application.Options;

/// <summary>
/// Platform-wide economics. Configured, never hardcoded, so ops can tune take-rate and
/// thresholds without a deploy (PRD §8, §9).
/// </summary>
public sealed class PlatformOptions
{
    public const string SectionName = "Platform";

    /// <summary>Commission as a fraction of settled credits, taken before the host is credited.</summary>
    public decimal CommissionRate { get; set; } = 0.12m;

    /// <summary>Sessions bill in whole increments; partial increments round up.</summary>
    public int BillingIncrementMinutes { get; set; } = 15;

    /// <summary>Shortest bookable duration.</summary>
    public int MinimumBookingMinutes { get; set; } = 30;

    /// <summary>Grace period after the booked end time before an uncovered overstay becomes a violation.</summary>
    public int OverstayGraceMinutes { get; set; } = 15;

    /// <summary>Hosts cannot cash out below this, to keep payout fees sane (PRD §5.1.6).</summary>
    public decimal MinimumCashOutCredits { get; set; } = 500m;

    /// <summary>Smallest recharge accepted.</summary>
    public decimal MinimumRechargeCredits { get; set; } = 100m;

    /// <summary>
    /// Largest recharge accepted in one go. Caps how much a fat-fingered or scripted order can
    /// park in the escrow account, and limits the damage from a hijacked session.
    /// </summary>
    public decimal MaximumRechargeCredits { get; set; } = 25_000m;

    /// <summary>Ceiling on the city-configured overstay multiplier, so hosts can't gouge a stuck renter.</summary>
    public decimal MaxOverstayMultiplier { get; set; } = 2.0m;
}
