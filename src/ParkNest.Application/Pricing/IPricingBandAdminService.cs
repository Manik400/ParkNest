using ParkNest.Domain.Common;
using ParkNest.Domain.Pricing;

namespace ParkNest.Application.Pricing;

/// <summary>
/// Band management for operators (PRD §9, Phase 2). Every write here leaves an audit row; that is
/// the reason this is a service rather than the controller writing to the DbSet directly.
/// </summary>
public interface IPricingBandAdminService
{
    Task<IReadOnlyList<CityPricingConfig>> ListAsync(string? city, CancellationToken cancellationToken = default);

    /// <summary>Creates the band or edits it in place, recording what changed and who changed it.</summary>
    Task<CityPricingConfig> UpsertAsync(UpsertPricingBand request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches a band on or off. Separate from <see cref="UpsertAsync"/> because it is the one
    /// edit with a blast radius beyond the band itself — no active band means no listing in that
    /// city can be published or repriced at all.
    /// </summary>
    Task<CityPricingConfig> SetActiveAsync(Guid bandId, bool isActive, string? reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// The edit history, newest first. Scoped to one band when <paramref name="bandId"/> is given,
    /// otherwise the whole platform's recent changes.
    /// </summary>
    Task<IReadOnlyList<PricingBandChange>> HistoryAsync(Guid? bandId, int limit = 100, CancellationToken cancellationToken = default);
}

public sealed record UpsertPricingBand(
    string City,
    string? Zone,
    VehicleType VehicleType,
    decimal MinPricePerHour,
    decimal MaxPricePerHour,
    decimal OverstayMultiplier = 1.0m,
    bool IsActive = true,
    string? Reason = null);
