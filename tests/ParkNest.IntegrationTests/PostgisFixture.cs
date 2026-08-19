using Microsoft.EntityFrameworkCore;
using ParkNest.Infrastructure.Persistence;

namespace ParkNest.IntegrationTests;

/// <summary>
/// A real Postgres with PostGIS, or nothing.
///
/// The unit suite runs on SQLite and therefore cannot touch the generated <c>geog</c> column, the
/// GiST index, or the raw SQL in <c>PostgresSpaceSearchService</c> — the entire geo-search path is
/// invisible to it (ADR 0003). These tests are what covers it, and they need the real thing.
///
/// The connection string comes from <c>PARKNEST_TEST_CONNECTION</c>. Without it the tests skip
/// rather than fail: a developer with no database running should not see a red suite for code they
/// did not touch. CI sets the variable, so the coverage is not optional where it counts.
/// </summary>
public sealed class PostgisFixture : IAsyncLifetime
{
    public const string ConnectionVariable = "PARKNEST_TEST_CONNECTION";

    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionVariable);

    public static bool Available => !string.IsNullOrWhiteSpace(ConnectionString);

    public ParkNestDbContext Db { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!Available)
        {
            return;
        }

        var options = new DbContextOptionsBuilder<ParkNestDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        Db = new ParkNestDbContext(options);

        // Migrate rather than EnsureCreated: the PostGIS column, the GiST index and the ledger
        // trigger all live in migration SQL, and EnsureCreated builds from the model, so it would
        // produce a schema missing precisely the things under test.
        await Db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (Available)
        {
            await Db.DisposeAsync();
        }
    }
}

/// <summary>
/// A fact that skips itself when there is no database to talk to. xUnit has no conditional
/// attribute, and the alternative — returning early from the test body — reports a pass for
/// something that never ran, which is worse than either failing or skipping.
/// </summary>
public sealed class RequiresPostgisFactAttribute : FactAttribute
{
    public RequiresPostgisFactAttribute()
    {
        if (!PostgisFixture.Available)
        {
            Skip = $"Set {PostgisFixture.ConnectionVariable} to run the PostGIS integration tests.";
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgisCollection : ICollectionFixture<PostgisFixture>
{
    public const string Name = "postgis";
}
