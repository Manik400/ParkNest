using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Abstractions;
using ParkNest.Domain.Common;

namespace ParkNest.Application.Wallets;

/// <param name="Total">Commission kept, less what disputes gave back, since the ledger began.</param>
/// <param name="ThisMonth">The same figure for the current calendar month, UTC.</param>
/// <param name="Earned">Commission credited before any dispute took some back.</param>
/// <param name="GivenBack">Refunds charged to the platform by dispute decisions.</param>
/// <param name="Settlements">Sessions and late cancellations that paid commission.</param>
public sealed record PlatformRevenue(
    decimal Total,
    decimal ThisMonth,
    decimal Earned,
    decimal GivenBack,
    int Settlements);

public interface IPlatformRevenueQueries
{
    /// <summary>What the platform has kept. Admin only.</summary>
    Task<PlatformRevenue> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the platform's own account off the ledger.
///
/// The ledger, not a counter: <c>PlatformRevenue</c> is a system account with no stored balance,
/// so what the platform has earned is exactly the sum of what was posted to it — every commission
/// as a credit, every dispute refund charged to the platform as a debit. Nothing to reconcile,
/// because there is no second copy of the number.
/// </summary>
public sealed class PlatformRevenueQueries : IPlatformRevenueQueries
{
    private readonly IParkNestDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public PlatformRevenueQueries(IParkNestDbContext db, ICurrentUser currentUser, IClock clock)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<PlatformRevenue> GetAsync(CancellationToken cancellationToken = default)
    {
        _currentUser.RequireAdmin();

        // Read the account's rows and add them up here rather than in SQL. Two reasons: the
        // reconciliation sweep already replays balances this way, so the platform's own figure is
        // computed the same way as everyone else's; and SQLite, which the unit suite runs on,
        // cannot sum a decimal. One row per settlement is not a table worth a server-side SUM.
        var rows = await _db.LedgerEntries
            .AsNoTracking()
            .Where(e => e.AccountType == LedgerAccountType.PlatformRevenue)
            .Select(e => new { e.Direction, e.Amount, e.CreatedAt })
            .ToListAsync(cancellationToken);

        var earned = rows.Where(r => r.Direction == EntryDirection.Credit).Sum(r => r.Amount);
        var givenBack = rows.Where(r => r.Direction == EntryDirection.Debit).Sum(r => r.Amount);
        var settlements = rows.Count(r => r.Direction == EntryDirection.Credit);

        var now = _clock.UtcNow;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var month = rows.Where(r => r.CreatedAt >= monthStart).ToList();

        var monthCredits = month.Where(r => r.Direction == EntryDirection.Credit).Sum(r => r.Amount);
        var monthDebits = month.Where(r => r.Direction == EntryDirection.Debit).Sum(r => r.Amount);

        return new PlatformRevenue(
            Money.Round(earned - givenBack),
            Money.Round(monthCredits - monthDebits),
            Money.Round(earned),
            Money.Round(givenBack),
            settlements);
    }
}
