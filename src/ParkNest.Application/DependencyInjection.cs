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
        services.AddScoped<Ratings.IRatingService, Ratings.RatingService>();
        services.AddScoped<Notifications.INotificationService, Notifications.NotificationService>();
        services.AddScoped<Notifications.IDeviceTokenService, Notifications.DeviceTokenService>();
        services.AddScoped<Users.IKycService, Users.KycService>();

        // One class handling five events is registered once per event type. Registered as the
        // interface rather than the class so the bus can resolve every subscriber to a given fact
        // without knowing who they are.
        services.AddScoped<Notifications.BookingNotificationHandlers>();
        services.AddScoped<IEventHandler<BookingCreated>>(sp => sp.GetRequiredService<Notifications.BookingNotificationHandlers>());
        services.AddScoped<IEventHandler<SessionStarted>>(sp => sp.GetRequiredService<Notifications.BookingNotificationHandlers>());
        services.AddScoped<IEventHandler<SessionEnded>>(sp => sp.GetRequiredService<Notifications.BookingNotificationHandlers>());
        services.AddScoped<IEventHandler<BookingCancelled>>(sp => sp.GetRequiredService<Notifications.BookingNotificationHandlers>());
        services.AddScoped<IEventHandler<OverstayCharged>>(sp => sp.GetRequiredService<Notifications.BookingNotificationHandlers>());
        services.AddScoped<IEventHandler<DisputeResolved>>(sp => sp.GetRequiredService<Notifications.BookingNotificationHandlers>());
        services.AddScoped<IEventHandler<KycReviewed>>(sp => sp.GetRequiredService<Notifications.BookingNotificationHandlers>());

        return services;
    }
}
