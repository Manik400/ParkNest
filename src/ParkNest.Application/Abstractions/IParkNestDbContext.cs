using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ParkNest.Domain.Bookings;
using ParkNest.Domain.Disputes;
using ParkNest.Domain.Listings;
using ParkNest.Domain.Payments;
using ParkNest.Domain.Payouts;
using ParkNest.Domain.Pricing;
using ParkNest.Domain.Ratings;
using ParkNest.Domain.Users;
using ParkNest.Domain.Wallets;

namespace ParkNest.Application.Abstractions;

/// <summary>
/// The persistence surface the application services code against. Keeping it an interface lets
/// the services stay provider-agnostic — Postgres in production, SQLite in tests.
/// </summary>
public interface IParkNestDbContext
{
    DbSet<User> Users { get; }
    DbSet<Vehicle> Vehicles { get; }
    DbSet<OtpCode> OtpCodes { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<ParkingSpace> ParkingSpaces { get; }
    DbSet<AvailabilityWindow> AvailabilityWindows { get; }
    DbSet<SpacePhoto> SpacePhotos { get; }
    DbSet<AvailabilityBlackout> AvailabilityBlackouts { get; }
    DbSet<Booking> Bookings { get; }
    DbSet<Wallet> Wallets { get; }
    DbSet<LedgerTransaction> LedgerTransactions { get; }
    DbSet<LedgerEntry> LedgerEntries { get; }
    DbSet<CityPricingConfig> CityPricingConfigs { get; }
    DbSet<Payout> Payouts { get; }
    DbSet<PaymentOrder> PaymentOrders { get; }
    DbSet<Dispute> Disputes { get; }
    DbSet<Rating> Ratings { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}
