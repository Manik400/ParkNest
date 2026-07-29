using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Abstractions;
using ParkNest.Domain.Common;
using ParkNest.Domain.Pricing;

namespace ParkNest.Api.Controllers;

/// <summary>
/// Price bands are operational data, not code (PRD §9) — ops changes them here, never by deploy.
/// </summary>
[ApiController]
[Route("api/admin/pricing")]
public sealed class AdminPricingController : ControllerBase
{
    private readonly IParkNestDbContext _db;
    private readonly IClock _clock;
    private readonly ICurrentUser _currentUser;

    public AdminPricingController(IParkNestDbContext db, IClock clock, ICurrentUser currentUser)
    {
        _db = db;
        _clock = clock;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CityPricingConfig>>> List(
        [FromQuery] string? city,
        CancellationToken cancellationToken)
    {
        _currentUser.RequireAdmin();

        var query = _db.CityPricingConfigs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(city))
        {
            query = query.Where(c => c.City == city);
        }

        return Ok(await query.OrderBy(c => c.City).ThenBy(c => c.Zone).ToListAsync(cancellationToken));
    }

    [HttpPut]
    public async Task<ActionResult<CityPricingConfig>> Upsert(
        [FromBody] UpsertBandRequest request,
        CancellationToken cancellationToken)
    {
        _currentUser.RequireAdmin();

        if (request.MinPricePerHour > request.MaxPricePerHour)
        {
            throw new DomainException("Minimum price cannot exceed maximum price.");
        }

        var existing = await _db.CityPricingConfigs.FirstOrDefaultAsync(
            c => c.City == request.City && c.Zone == request.Zone && c.VehicleType == request.VehicleType,
            cancellationToken);

        if (existing is null)
        {
            existing = new CityPricingConfig
            {
                City = request.City,
                Zone = request.Zone,
                VehicleType = request.VehicleType
            };
            _db.CityPricingConfigs.Add(existing);
        }

        existing.MinPricePerHour = Money.Round(request.MinPricePerHour);
        existing.MaxPricePerHour = Money.Round(request.MaxPricePerHour);
        existing.OverstayMultiplier = request.OverstayMultiplier;
        existing.IsActive = request.IsActive;
        existing.UpdatedAt = _clock.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(existing);
    }
}

public sealed record UpsertBandRequest(
    string City,
    string? Zone,
    VehicleType VehicleType,
    decimal MinPricePerHour,
    decimal MaxPricePerHour,
    decimal OverstayMultiplier = 1.0m,
    bool IsActive = true);
