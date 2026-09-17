using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Pricing;
using ParkNest.Domain.Common;
using ParkNest.Domain.Pricing;

namespace ParkNest.Api.Controllers;

/// <summary>
/// Price bands are operational data, not code (PRD §9) — ops changes them here, never by deploy.
/// Which is exactly why every change is recorded: a band that moves without a deploy also moves
/// without a commit, and <c>/history</c> is the only thing standing between that and a price
/// nobody can account for.
/// </summary>
[ApiController]
[Route("api/admin/pricing")]
public sealed class AdminPricingController : ControllerBase
{
    private readonly IPricingBandAdminService _bands;

    public AdminPricingController(IPricingBandAdminService bands) => _bands = bands;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CityPricingConfig>>> List(
        [FromQuery] string? city,
        CancellationToken cancellationToken) =>
        Ok(await _bands.ListAsync(city, cancellationToken));

    [HttpPut]
    public async Task<ActionResult<CityPricingConfig>> Upsert(
        [FromBody] UpsertBandRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _bands.UpsertAsync(
            new UpsertPricingBand(
                request.City,
                request.Zone,
                request.VehicleType,
                request.MinPricePerHour,
                request.MaxPricePerHour,
                request.OverstayMultiplier,
                request.IsActive,
                request.Reason),
            cancellationToken));

    /// <summary>
    /// Switches a band on or off without touching its numbers, so the prices are still there to
    /// read when somebody asks what this city used to allow.
    /// </summary>
    [HttpPost("{bandId:guid}/active")]
    public async Task<ActionResult<CityPricingConfig>> SetActive(
        Guid bandId,
        [FromBody] SetBandActiveRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _bands.SetActiveAsync(bandId, request.IsActive, request.Reason, cancellationToken));

    /// <summary>Every recorded edit, newest first. Whole platform unless a band is named.</summary>
    [HttpGet("history")]
    public async Task<ActionResult<IReadOnlyList<PricingBandChange>>> History(
        [FromQuery] Guid? bandId,
        [FromQuery] int limit,
        CancellationToken cancellationToken) =>
        Ok(await _bands.HistoryAsync(bandId, limit <= 0 ? 100 : limit, cancellationToken));
}

public sealed record UpsertBandRequest(
    string City,
    string? Zone,
    VehicleType VehicleType,
    decimal MinPricePerHour,
    decimal MaxPricePerHour,
    decimal OverstayMultiplier = 1.0m,
    bool IsActive = true,
    string? Reason = null);

public sealed record SetBandActiveRequest(bool IsActive, string? Reason = null);
