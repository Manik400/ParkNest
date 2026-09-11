using ParkNest.Domain.Common;

namespace ParkNest.Domain.Pricing;

/// <summary>
/// One recorded edit to a price band (PRD §9, Phase 2).
///
/// Bands are operational data that ops changes without a deploy, which is the point of them — but
/// it also means a band can move with nothing in git to show for it. A renter disputing what they
/// were charged, or a host asking why their listing stopped validating, is asking a question about
/// a row that has since been overwritten. This table is the answer: append-only, one row per
/// change, holding both sides of it and who made it.
///
/// The band row itself stays current-state. History lives here rather than as versioned band rows
/// because every read path wants the band in force *now*, and making them all filter on a validity
/// window would be a tax on the hot path to serve the rare question.
/// </summary>
public class PricingBandChange
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The band this edit landed on. Deliberately not a foreign key with a cascade: the audit row
    /// has to outlive the band, or deleting a band would erase the record of what it used to be.
    /// </summary>
    public Guid CityPricingConfigId { get; set; }

    /// <summary>
    /// The band's identity, copied. Answering "what did Bengaluru/Indiranagar look like in March"
    /// must not require the band row to still exist, or to still carry the same city and zone.
    /// </summary>
    public string City { get; set; } = string.Empty;

    public string? Zone { get; set; }

    public VehicleType VehicleType { get; set; }

    public PricingBandChangeKind Kind { get; set; }

    /// <summary>Null for a <see cref="PricingBandChangeKind.Created"/> row — there was no before.</summary>
    public decimal? PreviousMinPricePerHour { get; set; }
    public decimal? PreviousMaxPricePerHour { get; set; }
    public decimal? PreviousOverstayMultiplier { get; set; }
    public bool? PreviousIsActive { get; set; }

    public decimal MinPricePerHour { get; set; }
    public decimal MaxPricePerHour { get; set; }
    public decimal OverstayMultiplier { get; set; }
    public bool IsActive { get; set; }

    /// <summary>
    /// The admin who made the change. Not nullable and not a display name — an audit row whose
    /// author is optional is one that can be written without an author.
    /// </summary>
    public Guid ChangedByUserId { get; set; }

    /// <summary>
    /// Why, in the operator's own words. Optional because forcing a note produces "update", but
    /// asked for on every edit because the reason is the part the table cannot reconstruct.
    /// </summary>
    public string? Reason { get; set; }

    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum PricingBandChangeKind
{
    Created = 1,
    Updated = 2,

    /// <summary>
    /// Kept distinct from <see cref="Updated"/>. Deactivating a band stops every listing in that
    /// city validating, which is the single most disruptive thing this screen can do, and it
    /// should be findable in the history without reading each row's before-and-after.
    /// </summary>
    Deactivated = 3,

    Reactivated = 4
}
