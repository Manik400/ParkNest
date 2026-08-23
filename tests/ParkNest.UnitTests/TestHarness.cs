using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Auth;
using ParkNest.Application.Bookings;
using ParkNest.Application.Listings;
using ParkNest.Application.Options;
using ParkNest.Application.Pricing;
using ParkNest.Application.Wallets;
using ParkNest.Domain.Common;
using ParkNest.Domain.Listings;
using ParkNest.Domain.Pricing;
using ParkNest.Domain.Users;
using ParkNest.Infrastructure.Persistence;

namespace ParkNest.UnitTests;

/// <summary>Advanceable clock, so overstay scenarios don't need real elapsed time.</summary>
public sealed class TestClock : IClock
{
    public TestClock(DateTimeOffset start) => UtcNow = start;

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

/// <summary>
/// Wires the real services against an in-memory SQLite database. SQLite (not the EF in-memory
/// provider) so unique indexes, precision and transactions behave like a real relational store.
/// </summary>
public sealed class TestHarness : IDisposable
{
    public static readonly DateTimeOffset Origin = new(2026, 7, 28, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;

    public TestHarness(PlatformOptions? options = null)
    {
        Options = options ?? new PlatformOptions
        {
            CommissionRate = 0.10m,
            BillingIncrementMinutes = 15,
            MinimumBookingMinutes = 30,
            MinimumCashOutCredits = 500m,
            MinimumRechargeCredits = 100m,
            MaxOverstayMultiplier = 2.0m
        };

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<ParkNestDbContext>()
            .UseSqlite(_connection)
            .Options;

        Db = new ParkNestDbContext(dbOptions);
        Db.Database.EnsureCreated();

        Clock = new TestClock(Origin);
        CurrentUser = new TestCurrentUser();
        var wrapped = Microsoft.Extensions.Options.Options.Create(Options);

        Ledger = new LedgerService(Db, Clock);
        Wallets = new WalletService(Db, Ledger, Clock, wrapped);
        PricingService = new PricingService(Db, wrapped);
        // The no-op invalidator, because the unit suite runs with caching off. What the decorator
        // itself does is covered separately in SpaceSearchCacheTests, against a real cache.
        Listings = new ListingService(
            Db, PricingService, Clock, CurrentUser, new NoOpSpaceSearchCacheInvalidator());
        Events = new RecordingEventBus();
        Bookings = new BookingService(Db, Wallets, PricingService, Clock, CurrentUser, wrapped, Events);

        AuthOptions = new AuthOptions
        {
            SigningKey = "test-signing-key-that-is-long-enough-32",
            OtpPepper = "test-otp-pepper-value",
            OtpLifetimeMinutes = 5,
            OtpMaxAttempts = 3
        };

        OtpSender = new RecordingOtpSender();
        Auth = new AuthService(
            Db,
            new FakeTokenService(),
            OtpSender,
            Clock,
            Microsoft.Extensions.Options.Options.Create(AuthOptions),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthService>.Instance);
    }

    public PlatformOptions Options { get; }
    public ParkNestDbContext Db { get; }
    public TestClock Clock { get; }
    public TestCurrentUser CurrentUser { get; }
    public ILedgerService Ledger { get; }
    public IWalletService Wallets { get; }
    public IPricingService PricingService { get; }
    public IListingService Listings { get; }
    public IBookingService Bookings { get; }

    /// <summary>Captures what was announced, so tests can assert on it without a broker.</summary>
    public RecordingEventBus Events { get; }
    public IAuthService Auth { get; }
    public AuthOptions AuthOptions { get; }
    public RecordingOtpSender OtpSender { get; }

    public async Task<User> AddUserAsync(UserRole role, KycStatus kyc = KycStatus.NotStarted)
    {
        var user = new User
        {
            Role = role,
            FullName = $"{role} {Guid.NewGuid():N}"[..20],
            Phone = Random.Shared.NextInt64(9000000000, 9999999999).ToString(),
            KycStatus = kyc
        };

        Db.Users.Add(user);
        await Db.SaveChangesAsync();
        CurrentUser.SignIn(user.Id, role);
        return user;
    }

    public async Task<Vehicle> AddVehicleAsync(Guid userId, VehicleType type = VehicleType.FourWheeler)
    {
        var vehicle = new Vehicle
        {
            UserId = userId,
            Type = type,
            PlateNumber = $"KA01{Random.Shared.Next(1000, 9999)}"
        };

        Db.Vehicles.Add(vehicle);
        await Db.SaveChangesAsync();
        return vehicle;
    }

    public async Task<CityPricingConfig> AddBandAsync(
        string city = "Bengaluru",
        VehicleType type = VehicleType.FourWheeler,
        decimal min = 20m,
        decimal max = 120m,
        decimal overstayMultiplier = 1.0m)
    {
        var band = new CityPricingConfig
        {
            City = city,
            Zone = null,
            VehicleType = type,
            MinPricePerHour = min,
            MaxPricePerHour = max,
            OverstayMultiplier = overstayMultiplier,
            IsActive = true
        };

        Db.CityPricingConfigs.Add(band);
        await Db.SaveChangesAsync();
        return band;
    }

    /// <summary>UTC keeps wall-clock reasoning out of tests that are not about time zones.</summary>
    public const string TimeZone = "UTC";

    public async Task<ParkingSpace> AddPublishedSpaceAsync(
        Guid hostId,
        decimal pricePerHour = 60m,
        VehicleType type = VehicleType.FourWheeler,
        string city = "Bengaluru",
        IReadOnlyList<AvailabilityWindowRequest>? availabilityWindows = null)
    {
        CurrentUser.SignIn(hostId, UserRole.Host);

        // Open around the clock every day unless a test says otherwise, so availability does not
        // become an incidental variable in tests that are really about money or booking state.
        // Equal start and end means a full 24 hours, so these merge into one continuous interval
        // with no gap at midnight.
        var windows = availabilityWindows ?? Enum.GetValues<DayOfWeek>()
            .Select(d => new AvailabilityWindowRequest(d, new TimeOnly(0, 0), new TimeOnly(0, 0)))
            .ToArray();

        var space = await Listings.CreateDraftAsync(new CreateListingRequest(
            "Driveway", "12 Main Rd", city, null, 12.97, 77.59, pricePerHour, new[] { type },
            windows, TimeZone));

        return await Listings.PublishAsync(space.Id);
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}
