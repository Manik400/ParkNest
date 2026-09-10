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

    /// <summary>
    /// Trust score a host needs before money may leave the platform.
    ///
    /// Cash-out is the one movement nothing can compensate for cheaply, so it is the right place
    /// for the score to bite — and until now it bit nowhere, which made it a number the system
    /// computed and never used.
    ///
    /// Forty is deliberately far down. A rating moves the score by at most six and a violation by
    /// ten, so no single annoyed renter, and no bad week, can strand a host's earnings; reaching
    /// the floor takes a sustained pattern. Zero disables the gate.
    /// </summary>
    public int MinimumTrustScoreForCashOut { get; set; } = 40;

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

    /// <summary>
    /// How far from the pin a Tier 2 scan is still accepted.
    ///
    /// Generous on purpose. Phone GPS is routinely tens of metres out in exactly the places this
    /// matters — basements, between tall buildings, under cover — and a renter who is genuinely
    /// standing at the space being told they are not is a support ticket and a lost booking. The
    /// check is corroboration of a scan that already required physical presence, not the sole
    /// control, so it is tuned to avoid false refusals rather than to catch every spoof.
    /// </summary>
    public int CheckInRadiusMetres { get; set; } = 150;

    /// <summary>
    /// How far ahead of a booking's start an over-running session counts as blocking it.
    ///
    /// It exists because the useful moment to tell someone is before they arrive, not once they
    /// are circling. Fifteen minutes is roughly the drive that is already under way.
    ///
    /// Deliberately independent of <see cref="OverstayGraceMinutes"/>: grace decides when a charge
    /// lands and is a courtesy to the renter who is late, and it has nothing to say about whether
    /// somebody else's slot is occupied. Zero disables the check.
    /// </summary>
    public int BlockedSlotLookaheadMinutes { get; set; } = 15;

    /// <summary>Ceiling on the city-configured overstay multiplier, so hosts can't gouge a stuck renter.</summary>
    public decimal MaxOverstayMultiplier { get; set; } = 2.0m;
}
