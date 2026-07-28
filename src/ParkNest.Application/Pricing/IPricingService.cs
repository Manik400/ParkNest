using ParkNest.Domain.Common;
using ParkNest.Domain.Pricing;

namespace ParkNest.Application.Pricing;

public interface IPricingService
{
    /// <summary>Resolves the active band, preferring a zone-specific row over the city-wide one.</summary>
    Task<CityPricingConfig> GetBandAsync(string city, string? zone, VehicleType vehicleType, CancellationToken cancellationToken = default);

    /// <summary>Throws if the host's asking price falls outside the active band (PRD §9).</summary>
    Task ValidateListingPriceAsync(string city, string? zone, VehicleType vehicleType, decimal pricePerHour, CancellationToken cancellationToken = default);

    /// <summary>Rounds a duration up to the billing increment and prices it.</summary>
    Quote QuoteBooking(decimal ratePerHour, int requestedMinutes);

    /// <summary>Prices minutes beyond the booked window at the overstay rate.</summary>
    decimal QuoteOverstay(decimal ratePerHour, decimal overstayMultiplier, int overstayMinutes);

    /// <summary>Rounds a raw duration up to the next whole billing increment.</summary>
    int RoundUpToIncrement(int minutes);
}

/// <param name="BilledMinutes">Duration after rounding up to the billing increment.</param>
/// <param name="Amount">Credits to reserve for that duration.</param>
public readonly record struct Quote(int BilledMinutes, decimal Amount);
