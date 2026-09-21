using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Admin;
using ParkNest.Domain.Common;
using ParkNest.Domain.Listings;
using ParkNest.Domain.Pricing;
using ParkNest.Domain.Users;

namespace ParkNest.Infrastructure.Persistence;

/// <summary>
/// Empties the operational tables in one statement.
///
/// The table list is read off the EF model rather than written down, so a table added next month
/// is wiped next month too — the failure mode of a hand-kept list is a reset that leaves a
/// dangling foreign key nobody notices until the next booking. What survives is named
/// explicitly, because that is the list worth reading: accounts, sessions, and the price bands.
/// </summary>
public sealed class PostgresDataWiper : IDataWiper
{
    /// <summary>What a reset leaves alone. Everything else in the model goes.</summary>
    private static readonly Type[] Kept =
    {
        typeof(User),
        typeof(RefreshToken),
        typeof(CityPricingConfig),
        typeof(PricingBandChange),
    };

    private readonly ParkNestDbContext _db;

    public PostgresDataWiper(ParkNestDbContext db) => _db = db;

    public async Task<DataResetResult> WipeAsync(CancellationToken cancellationToken = default)
    {
        var tables = _db.Model.GetEntityTypes()
            .Where(t => !t.IsOwned() && t.GetTableName() is not null && !Kept.Contains(t.ClrType))
            .Select(t => t.GetTableName()!)
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        var removed = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var table in tables)
        {
            // Quoted, because EF names these in snake_case and Postgres would fold anything else.
            var count = await _db.Database
                .SqlQueryRaw<long>($"SELECT count(*) AS \"Value\" FROM \"{table}\"")
                .FirstAsync(cancellationToken);

            removed[table] = count;
        }

        // One TRUNCATE for the lot. CASCADE only reaches tables that reference these, and every
        // such table is itself in the list — users are referenced by wallets, not the other way
        // round — so the kept tables cannot be touched by it. RESTART IDENTITY so the analytics
        // sequence begins again at one rather than carrying on from the old count.
        var list = string.Join(", ", tables.Select(t => $"\"{t}\""));
        await _db.Database.ExecuteSqlRawAsync($"TRUNCATE TABLE {list} RESTART IDENTITY CASCADE", cancellationToken);

        // The submissions are gone, so the status derived from them goes back to the start, and a
        // trust score built on sessions that no longer exist is not worth keeping either.
        await _db.Users.ExecuteUpdateAsync(
            u => u.SetProperty(x => x.KycStatus, KycStatus.NotStarted).SetProperty(x => x.TrustScore, 100),
            cancellationToken);

        var kept = Kept
            .Select(t => _db.Model.FindEntityType(t)?.GetTableName())
            .Where(t => t is not null)
            .Select(t => t!)
            .ToList();

        return new DataResetResult(removed, kept);
    }
}
