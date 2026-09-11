using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ParkNest.Domain.Bookings;
using ParkNest.Domain.Disputes;
using ParkNest.Domain.Listings;
using ParkNest.Domain.Notifications;
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
    DbSet<PricingBandChange> PricingBandChanges { get; }
    DbSet<Payout> Payouts { get; }
    DbSet<PaymentOrder> PaymentOrders { get; }
    DbSet<Dispute> Disputes { get; }

    DbSet<DisputeEvidence> DisputeEvidence { get; }
    DbSet<Rating> Ratings { get; }
    DbSet<Notification> Notifications { get; }

    DbSet<DeviceToken> DeviceTokens { get; }

    DbSet<KycSubmission> KycSubmissions { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops tracking one entity, so the next query reads it from the database again.
    ///
    /// Exists for optimistic-concurrency retries: after a failed save the tracked copy holds the
    /// values that lost, and re-querying would hand back that same stale instance. Deliberately
    /// per-entity rather than a wholesale clear — callers hold references to entities they are
    /// still going to modify and save, and detaching those would silently discard their work.
    /// </summary>
    void Detach(object entity);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>True when a transaction is already open, so a caller does not try to nest one.</summary>
    bool HasActiveTransaction { get; }

    /// <summary>
    /// Takes a write lock on these wallet rows for the rest of the current transaction, so two
    /// writers touching the same wallet queue rather than race.
    ///
    /// Optimistic concurrency alone is not enough here. It detects a lost update and retries, but
    /// under real contention the retries collide too, and a renter whose booking failed because
    /// five other people settled at the same instant has been told something untrue. Locking is
    /// the right trade for a ledger: these rows are per-user, so the queue is short, and
    /// correctness is worth more than throughput on the one table that holds money.
    ///
    /// A no-op on providers without row locks — the SQLite test database serialises writes anyway.
    /// </summary>
    Task LockWalletsAsync(IReadOnlyList<Guid> walletIds, CancellationToken cancellationToken = default);
}
