using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ParkNest.Application.Abstractions;
using ParkNest.Domain.Analytics;
using ParkNest.Domain.Bookings;
using ParkNest.Domain.Common;
using ParkNest.Domain.Disputes;
using ParkNest.Domain.Listings;
using ParkNest.Domain.Notifications;
using ParkNest.Domain.Payments;
using ParkNest.Domain.Payouts;
using ParkNest.Domain.Pricing;
using ParkNest.Domain.Ratings;
using ParkNest.Domain.Users;
using ParkNest.Domain.Wallets;

namespace ParkNest.Infrastructure.Persistence;

public class ParkNestDbContext : DbContext, IParkNestDbContext
{
    public ParkNestDbContext(DbContextOptions<ParkNestDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ParkingSpace> ParkingSpaces => Set<ParkingSpace>();
    public DbSet<SpaceVehicleSupport> SpaceVehicleSupports => Set<SpaceVehicleSupport>();
    public DbSet<AvailabilityWindow> AvailabilityWindows => Set<AvailabilityWindow>();
    public DbSet<SpacePhoto> SpacePhotos => Set<SpacePhoto>();
    public DbSet<AvailabilityBlackout> AvailabilityBlackouts => Set<AvailabilityBlackout>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<LedgerTransaction> LedgerTransactions => Set<LedgerTransaction>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<CityPricingConfig> CityPricingConfigs => Set<CityPricingConfig>();
    public DbSet<PricingBandChange> PricingBandChanges => Set<PricingBandChange>();
    public DbSet<Payout> Payouts => Set<Payout>();
    public DbSet<PaymentOrder> PaymentOrders => Set<PaymentOrder>();
    public DbSet<Dispute> Disputes => Set<Dispute>();
    public DbSet<DisputeEvidence> DisputeEvidence => Set<DisputeEvidence>();
    public DbSet<Rating> Ratings => Set<Rating>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();
    public DbSet<KycSubmission> KycSubmissions => Set<KycSubmission>();
    public DbSet<AnalyticsEvent> AnalyticsEvents => Set<AnalyticsEvent>();
    public DbSet<CityRequest> CityRequests => Set<CityRequest>();

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        Database.BeginTransactionAsync(cancellationToken);

    public bool HasActiveTransaction => Database.CurrentTransaction is not null;

    public async Task LockWalletsAsync(
        IReadOnlyList<Guid> walletIds,
        CancellationToken cancellationToken = default)
    {
        // SQLite, used by the unit suite, has no row-level locking and does not need it: one
        // writer at a time is the whole storage model.
        if (walletIds.Count == 0 || Database.ProviderName?.Contains("Npgsql") != true)
        {
            return;
        }

        // Locked one at a time in id order. Two transactions that touch the same pair of wallets
        // in opposite orders would otherwise each hold what the other needs, and Postgres would
        // break the deadlock by killing one of them — a booking failing for a reason nobody could
        // act on. A consistent order makes that impossible rather than rare.
        foreach (var walletId in walletIds.OrderBy(id => id))
        {
            await Database.ExecuteSqlRawAsync(
                """SELECT 1 FROM wallets WHERE "Id" = {0} FOR UPDATE""",
                new object[] { walletId },
                cancellationToken);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ParkNestDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no native DateTimeOffset, so EF cannot translate range comparisons on it —
        // which the booking-overlap check depends on. Encoding to a binary long restores ordering.
        // Safe here because every timestamp the application writes is UTC. Postgres keeps its
        // native timestamptz mapping and is unaffected.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            configurationBuilder
                .Properties<DateTimeOffset>()
                .HaveConversion<DateTimeOffsetToBinaryConverter>();
        }

        base.ConfigureConventions(configurationBuilder);
    }

    public void Detach(object entity) => Entry(entity).State = EntityState.Detached;

    /// <summary>
    /// Postgres error code for an exclusion-constraint violation.
    /// </summary>
    private const string ExclusionViolation = "23P01";

    /// <summary>
    /// Name of the constraint that stops two live bookings covering the same minutes of one space.
    /// </summary>
    private const string BookingOverlapConstraint = "bookings_no_overlap";

    /// <summary>
    /// Turns the database's last word on double-booking into the same message the application
    /// check gives.
    ///
    /// The check in <c>BookingService</c> is a courtesy: it reads the table, then writes, and two
    /// requests can pass it at the same instant. The exclusion constraint is the guarantee, and it
    /// speaks Postgres — without this the loser of that race gets a 500 and a stack trace about a
    /// constraint they have never heard of, for something that is simply "somebody was faster".
    ///
    /// Overridden on the overload every other save funnels through, so no call path can slip past
    /// it into the untranslated exception.
    /// </summary>
    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: ExclusionViolation,
            ConstraintName: BookingOverlapConstraint
        })
        {
            throw new DomainException(
                "That space was just booked for part of your window. Pick another time.");
        }
    }
}
