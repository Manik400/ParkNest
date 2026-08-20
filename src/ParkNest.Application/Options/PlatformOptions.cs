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

    /// <summary>
    /// How long past the booked end the background meter leaves a session alone.
    ///
    /// It stops a renter two minutes late getting a debit while they are walking back to the car.
    /// It does not discount anything: checkout bills the real duration either way, so grace
    /// changes when the charge lands, never what it comes to.
    /// </summary>
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

    /// <summary>
    /// How long before the booked start a renter may cancel and get everything back.
    ///
    /// The number is a product decision, not a technical one, which is exactly why it lives here:
    /// ops can move it without a deploy once there is evidence about how often hosts lose slots.
    /// An hour is the starting position — long enough that a change of plans is free, short enough
    /// that a host is not left with a dead slot minutes before it starts.
    /// </summary>
    public int FreeCancellationMinutes { get; set; } = 60;

    /// <summary>
    /// Fraction of the hold the renter forfeits when they cancel inside that window. It settles to
    /// the host exactly as a session would, commission and all — a cancelled slot they could not
    /// re-let is lost income, and the platform's cut of compensating them is the same cut it takes
    /// on the booking that did not happen.
    ///
    /// Zero restores the old behaviour: cancel any time, keep everything.
    /// </summary>
    public decimal LateCancellationFeeRate { get; set; } = 0.5m;

    /// <summary>Ceiling on the city-configured overstay multiplier, so hosts can't gouge a stuck renter.</summary>
    public decimal MaxOverstayMultiplier { get; set; } = 2.0m;
}
