using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;
using ParkNest.Domain.Pricing;

namespace ParkNest.Application.Pricing;

public sealed class PricingBandAdminService : IPricingBandAdminService
{
    private readonly IParkNestDbContext _db;
    private readonly IClock _clock;
    private readonly ICurrentUser _currentUser;
    private readonly PlatformOptions _options;

    public PricingBandAdminService(
        IParkNestDbContext db,
        IClock clock,
        ICurrentUser currentUser,
        IOptions<PlatformOptions> options)
    {
        _db = db;
        _clock = clock;
        _currentUser = currentUser;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<CityPricingConfig>> ListAsync(string? city, CancellationToken cancellationToken = default)
    {
        _currentUser.RequireAdmin();

        var query = _db.CityPricingConfigs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(city))
        {
            query = query.Where(c => c.City == city);
        }

        return await query.OrderBy(c => c.City).ThenBy(c => c.Zone).ToListAsync(cancellationToken);
    }

    public async Task<CityPricingConfig> UpsertAsync(UpsertPricingBand request, CancellationToken cancellationToken = default)
    {
        _currentUser.RequireAdmin();
        var adminId = _currentUser.RequireUserId();

        var city = (request.City ?? string.Empty).Trim();

        // A zone of "" and a zone of null are two different rows to the unique index but the same
        // thing to an operator who cleared the field. Normalising here keeps a stray space from
        // quietly creating a second band that shadows the city-wide one.
        var zone = string.IsNullOrWhiteSpace(request.Zone) ? null : request.Zone.Trim();

        Validate(city, request.MinPricePerHour, request.MaxPricePerHour, request.OverstayMultiplier);

        var band = await _db.CityPricingConfigs.FirstOrDefaultAsync(
            c => c.City == city && c.Zone == zone && c.VehicleType == request.VehicleType,
            cancellationToken);

        var created = band is null;
        if (band is null)
        {
            band = new CityPricingConfig
            {
                City = city,
                Zone = zone,
                VehicleType = request.VehicleType
            };
            _db.CityPricingConfigs.Add(band);
        }

        // Captured before the assignments below overwrite them. This is the "from" half of the
        // audit row, and once the entity is mutated there is no way back to it.
        BandSnapshot? before = created ? null : Snapshot(band);

        band.MinPricePerHour = Money.Round(request.MinPricePerHour);
        band.MaxPricePerHour = Money.Round(request.MaxPricePerHour);
        band.OverstayMultiplier = request.OverstayMultiplier;
        band.IsActive = request.IsActive;
        band.UpdatedAt = _clock.UtcNow;

        var kind = created
            ? PricingBandChangeKind.Created
            : before!.Value.IsActive != band.IsActive
                ? (band.IsActive ? PricingBandChangeKind.Reactivated : PricingBandChangeKind.Deactivated)
                : PricingBandChangeKind.Updated;

        // An upsert that changed nothing still writes a row. That somebody opened this band and
        // saved it untouched is worth knowing when tracing how a price got where it is, and
        // suppressing no-op rows leaves gaps in the history that read as inactivity.
        Record(band, before, kind, adminId, request.Reason);

        await _db.SaveChangesAsync(cancellationToken);
        return band;
    }

    public async Task<CityPricingConfig> SetActiveAsync(
        Guid bandId,
        bool isActive,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        _currentUser.RequireAdmin();
        var adminId = _currentUser.RequireUserId();

        var band = await _db.CityPricingConfigs.FirstOrDefaultAsync(c => c.Id == bandId, cancellationToken)
            ?? throw new DomainException("Pricing band not found.");

        if (band.IsActive == isActive)
        {
            return band;
        }

        var before = Snapshot(band);
        band.IsActive = isActive;
        band.UpdatedAt = _clock.UtcNow;

        Record(
            band,
            before,
            isActive ? PricingBandChangeKind.Reactivated : PricingBandChangeKind.Deactivated,
            adminId,
            reason);

        await _db.SaveChangesAsync(cancellationToken);
        return band;
    }

    public async Task<IReadOnlyList<PricingBandChange>> HistoryAsync(
        Guid? bandId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        _currentUser.RequireAdmin();

        var query = _db.PricingBandChanges.AsQueryable();
        if (bandId is not null)
        {
            query = query.Where(c => c.CityPricingConfigId == bandId);
        }

        return await query
            .OrderByDescending(c => c.ChangedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);
    }

    private void Validate(string city, decimal min, decimal max, decimal overstayMultiplier)
    {
        if (string.IsNullOrWhiteSpace(city))
        {
            throw new DomainException("A pricing band needs a city.");
        }

        if (min <= 0m)
        {
            throw new DomainException("Minimum price must be greater than zero.");
        }

        if (min > max)
        {
            throw new DomainException("Minimum price cannot exceed maximum price.");
        }

        // Below 1.0 an over-run would cost less per minute than the booking did, which rewards
        // overstaying. That is the opposite of what the multiplier is for.
        if (overstayMultiplier < 1.0m)
        {
            throw new DomainException("Overstay multiplier cannot be below 1.0.");
        }

        // Refused at write time as well as at read time. PricingService already clamps it, but a
        // band saved above the ceiling is a trap that springs on the next booking, by which point
        // the operator who set it has moved on.
        if (overstayMultiplier > _options.MaxOverstayMultiplier)
        {
            throw new DomainException(
                $"Overstay multiplier {overstayMultiplier} exceeds the platform ceiling of {_options.MaxOverstayMultiplier}.");
        }
    }

    private void Record(
        CityPricingConfig band,
        BandSnapshot? before,
        PricingBandChangeKind kind,
        Guid adminId,
        string? reason)
    {
        _db.PricingBandChanges.Add(new PricingBandChange
        {
            CityPricingConfigId = band.Id,
            City = band.City,
            Zone = band.Zone,
            VehicleType = band.VehicleType,
            Kind = kind,
            PreviousMinPricePerHour = before?.MinPricePerHour,
            PreviousMaxPricePerHour = before?.MaxPricePerHour,
            PreviousOverstayMultiplier = before?.OverstayMultiplier,
            PreviousIsActive = before?.IsActive,
            MinPricePerHour = band.MinPricePerHour,
            MaxPricePerHour = band.MaxPricePerHour,
            OverstayMultiplier = band.OverstayMultiplier,
            IsActive = band.IsActive,
            ChangedByUserId = adminId,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            ChangedAt = _clock.UtcNow
        });
    }

    private static BandSnapshot Snapshot(CityPricingConfig band) =>
        new(band.MinPricePerHour, band.MaxPricePerHour, band.OverstayMultiplier, band.IsActive);

    private readonly record struct BandSnapshot(
        decimal MinPricePerHour,
        decimal MaxPricePerHour,
        decimal OverstayMultiplier,
        bool IsActive);
}
