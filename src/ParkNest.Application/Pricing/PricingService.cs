using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;
using ParkNest.Domain.Pricing;

namespace ParkNest.Application.Pricing;

public sealed class PricingService : IPricingService
{
    private readonly IParkNestDbContext _db;
    private readonly PlatformOptions _options;

    public PricingService(IParkNestDbContext db, IOptions<PlatformOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task<CityPricingConfig> GetBandAsync(string city, string? zone, VehicleType vehicleType, CancellationToken cancellationToken = default)
    {
        var candidates = await _db.CityPricingConfigs
            .Where(c => c.IsActive && c.City == city && c.VehicleType == vehicleType)
            .ToListAsync(cancellationToken);

        // A zone-specific band is more specific than the city-wide fallback, so it wins.
        var band = candidates.FirstOrDefault(c => c.Zone != null && c.Zone == zone)
                   ?? candidates.FirstOrDefault(c => c.Zone == null);

        if (band is null)
        {
            throw new DomainException($"No active pricing band configured for {city}/{zone ?? "*"} ({vehicleType}).");
        }

        if (band.OverstayMultiplier > _options.MaxOverstayMultiplier)
        {
            throw new DomainException(
                $"Configured overstay multiplier {band.OverstayMultiplier} exceeds the platform ceiling {_options.MaxOverstayMultiplier}.");
        }

        return band;
    }

    public async Task ValidateListingPriceAsync(string city, string? zone, VehicleType vehicleType, decimal pricePerHour, CancellationToken cancellationToken = default)
    {
        var band = await GetBandAsync(city, zone, vehicleType, cancellationToken);
        if (!band.Allows(pricePerHour))
        {
            throw new DomainException(
                $"Price {pricePerHour:0.00}/hr is outside the {city} band of {band.MinPricePerHour:0.00}–{band.MaxPricePerHour:0.00}/hr for {vehicleType}.");
        }
    }

    public Quote QuoteBooking(decimal ratePerHour, int requestedMinutes)
    {
        if (requestedMinutes < _options.MinimumBookingMinutes)
        {
            throw new DomainException($"Minimum booking duration is {_options.MinimumBookingMinutes} minutes.");
        }

        var billed = RoundUpToIncrement(requestedMinutes);
        return new Quote(billed, Money.Round(ratePerHour * billed / 60m));
    }

    public decimal QuoteOverstay(decimal ratePerHour, decimal overstayMultiplier, int overstayMinutes)
    {
        if (overstayMinutes <= 0)
        {
            return 0m;
        }

        var effectiveRate = ratePerHour * Math.Min(overstayMultiplier, _options.MaxOverstayMultiplier);
        return Money.Round(effectiveRate * overstayMinutes / 60m);
    }

    public int RoundUpToIncrement(int minutes)
    {
        var increment = _options.BillingIncrementMinutes;
        if (increment <= 0)
        {
            return minutes;
        }

        // Any started increment is a charged increment — the renter occupied the space for it.
        return (int)(Math.Ceiling(minutes / (decimal)increment) * increment);
    }
}
