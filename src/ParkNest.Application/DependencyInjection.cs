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
        services.AddScoped<IPricingBandAdminService, PricingBandAdminService>();
        services.AddScoped<IListingService, ListingService>();
        services.AddScoped<ICityService, CityService>();
        services.AddScoped<IListingPhotoService, ListingPhotoService>();
        services.AddScoped<IBookingService, BookingService>();
        services.AddScoped<IOverstayMeter, OverstayMeter>();
        services.AddScoped<Auth.IAuthService, Auth.AuthService>();
        services.AddScoped<Queries.IParkNestQueries, Queries.ParkNestQueries>();
        services.AddScoped<Vehicles.IVehicleService, Vehicles.VehicleService>();
        services.AddScoped<Payments.IPaymentService, Payments.PaymentService>();
        services.AddScoped<Payments.IPaymentSettlement, Payments.PaymentSettlement>();
        services.AddScoped<Payments.IPaymentReconciler, Payments.PaymentReconciler>();
        services.AddScoped<Payments.IPaymentOrderExpiry, Payments.PaymentOrderExpiry>();
        services.AddSingleton<Payments.PaymentUrls>();
        services.AddScoped<Disputes.IDisputeService, Disputes.DisputeService>();
        services.AddScoped<Ratings.IRatingService, Ratings.RatingService>();
        services.AddScoped<Notifications.INotificationService, Notifications.NotificationService>();
        services.AddScoped<Notifications.IDeviceTokenService, Notifications.DeviceTokenService>();
        services.AddScoped<Users.IKycService, Users.KycService>();
        services.AddScoped<Users.IProfileService, Users.ProfileService>();
        services.AddScoped<Analytics.IAnalyticsRecorder, Analytics.AnalyticsRecorder>();
        services.AddScoped<Analytics.IAnalyticsCollector, Analytics.AnalyticsCollector>();
        services.AddScoped<Analytics.IAnalyticsQueries, Analytics.AnalyticsQueries>();
        services.AddScoped<Analytics.IAnalyticsRetention, Analytics.AnalyticsRetention>();
        services.AddScoped<IPlatformRevenueQueries, PlatformRevenueQueries>();
        services.AddScoped<Admin.IDataResetService, Admin.DataResetService>();

        // One class handling several events is registered once per event type. Registered as the
        // interface rather than the class so the bus can resolve every subscriber to a given fact
        // without knowing who they are.
        services.AddScoped<Notifications.BookingNotificationHandlers>();
        services.AddScoped<IEventHandler<BookingCreated>>(sp => sp.GetRequiredService<Notifications.BookingNotificationHandlers>());
        services.AddScoped<IEventHandler<SessionStarted>>(sp => sp.GetRequiredService<Notifications.BookingNotificationHandlers>());
        services.AddScoped<IEventHandler<SessionEnded>>(sp => sp.GetRequiredService<Notifications.BookingNotificationHandlers>());
        services.AddScoped<IEventHandler<BookingCancelled>>(sp => sp.GetRequiredService<Notifications.BookingNotificationHandlers>());
        services.AddScoped<IEventHandler<OverstayCharged>>(sp => sp.GetRequiredService<Notifications.BookingNotificationHandlers>());
        services.AddScoped<IEventHandler<NextSlotBlocked>>(sp => sp.GetRequiredService<Notifications.BookingNotificationHandlers>());
        services.AddScoped<IEventHandler<DisputeResolved>>(sp => sp.GetRequiredService<Notifications.BookingNotificationHandlers>());
        services.AddScoped<IEventHandler<KycReviewed>>(sp => sp.GetRequiredService<Notifications.BookingNotificationHandlers>());

        // The site counter listens to the same facts. A third subscriber rather than a line
        // inside the notification handler, so switching analytics off never touches the
        // messages a renter actually receives.
        services.AddScoped<Analytics.AnalyticsEventHandlers>();
        services.AddScoped<IEventHandler<BookingCreated>>(sp => sp.GetRequiredService<Analytics.AnalyticsEventHandlers>());
        services.AddScoped<IEventHandler<BookingCancelled>>(sp => sp.GetRequiredService<Analytics.AnalyticsEventHandlers>());
        services.AddScoped<IEventHandler<SessionEnded>>(sp => sp.GetRequiredService<Analytics.AnalyticsEventHandlers>());
        services.AddScoped<IEventHandler<DisputeRaised>>(sp => sp.GetRequiredService<Analytics.AnalyticsEventHandlers>());

        return services;
    }
}
