using Microsoft.Extensions.DependencyInjection;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Bookings;
using ParkNest.Application.Listings;
using ParkNest.Application.Pricing;
using ParkNest.Application.Wallets;

namespace ParkNest.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddParkNestApplication(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<ILedgerService, LedgerService>();
        services.AddScoped<IWalletService, WalletService>();
        services.AddScoped<IPricingService, PricingService>();
        services.AddScoped<IListingService, ListingService>();
        services.AddScoped<IListingPhotoService, ListingPhotoService>();
        services.AddScoped<IBookingService, BookingService>();
        services.AddScoped<IOverstayMeter, OverstayMeter>();
        services.AddScoped<Auth.IAuthService, Auth.AuthService>();
        services.AddScoped<Queries.IParkNestQueries, Queries.ParkNestQueries>();
        services.AddScoped<Vehicles.IVehicleService, Vehicles.VehicleService>();
        services.AddScoped<Payments.IPaymentService, Payments.PaymentService>();
        services.AddScoped<Payments.IPaymentOrderExpiry, Payments.PaymentOrderExpiry>();
        services.AddScoped<Disputes.IDisputeService, Disputes.DisputeService>();

        return services;
    }
}
