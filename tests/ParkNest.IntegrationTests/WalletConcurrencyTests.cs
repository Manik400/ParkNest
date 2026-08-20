using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Application.Wallets;
using ParkNest.Domain.Common;
using ParkNest.Domain.Users;
using ParkNest.Infrastructure.Persistence;

namespace ParkNest.IntegrationTests;

/// <summary>
/// The optimistic <c>Version</c> token, against real contention.
///
/// This cannot be asked of the SQLite suite: every test there shares one connection and runs its
/// operations one after another, so the concurrency token is never actually tested — only carried.
/// Two renters settling against the same host, or one renter ending two sessions at once, are
/// ordinary events, and the question is whether they can lose an update between them.
///
/// Each task gets its own DbContext. A shared one would serialise the very thing under test, and
/// would also be misuse — a DbContext is not thread-safe.
/// </summary>
[Collection(PostgisCollection.Name)]
public sealed class WalletConcurrencyTests : IAsyncLifetime
{
    private readonly PostgisFixture _fixture;
    private readonly List<Guid> _users = new();

    public WalletConcurrencyTests(PostgisFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!PostgisFixture.Available || _users.Count == 0)
        {
            return;
        }

        // The ledger is append-only by trigger, so the entries and transactions this test wrote
        // stay. Only the users and their wallets are removed — enough that a rerun starts clean
        // without fighting an invariant the whole system rests on.
        var db = _fixture.Db;

        var wallets = await db.Wallets.Where(w => _users.Contains(w.UserId)).ToListAsync();
        db.Wallets.RemoveRange(wallets);

        var users = await db.Users.Where(u => _users.Contains(u.Id)).ToListAsync();
        db.Users.RemoveRange(users);

        await db.SaveChangesAsync();
    }

    private static PlatformOptions Options => new()
    {
        CommissionRate = 0.10m,
        MinimumRechargeCredits = 1m,
        MaximumRechargeCredits = 1_000_000m,
        MinimumCashOutCredits = 1m,
    };

    /// <summary>A context and wallet service of its own, as a separate request would have.</summary>
    private static (ParkNestDbContext Db, IWalletService Wallets) NewScope()
    {
        var options = new DbContextOptionsBuilder<ParkNestDbContext>()
            .UseNpgsql(PostgisFixture.ConnectionString)
            .Options;

        var db = new ParkNestDbContext(options);
        var clock = new SystemClock();
        var ledger = new LedgerService(db, clock);

        return (db, new WalletService(db, ledger, clock, Microsoft.Extensions.Options.Options.Create(Options)));
    }

    private async Task<Guid> AddFundedUserAsync(decimal credits)
    {
        var (db, wallets) = NewScope();
        await using var _ = db;

        var user = new User
        {
            Role = UserRole.Both,
            FullName = "Concurrency",
            Phone = Random.Shared.NextInt64(9000000000, 9999999999).ToString(),
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        _users.Add(user.Id);

        await wallets.RechargeAsync(user.Id, credits, $"seed:{user.Id}");

        return user.Id;
    }

    [RequiresPostgisFact]
    public async Task Concurrent_holds_against_one_wallet_all_land()
    {
        // Twenty simultaneous holds of 10 against a balance of 1000. Every one is affordable, so
        // every one must succeed — losing any of them would mean a renter was told a space was
        // reserved when it was not.
        var userId = await AddFundedUserAsync(1_000m);

        var holds = Enumerable.Range(0, 20).Select(async i =>
        {
            var (db, wallets) = NewScope();
            await using var _ = db;

            await wallets.PlaceHoldAsync(userId, Guid.NewGuid(), 10m, $"hold:{userId}:{i}");
        });

        await Task.WhenAll(holds);

        var (check, checkWallets) = NewScope();
        await using var __ = check;

        var wallet = await checkWallets.GetOrCreateWalletAsync(userId);

        wallet.SpendableBalance.Should().Be(800m);
        wallet.HeldBalance.Should().Be(200m, "all twenty holds must be reflected, not just the winners");
    }

    [RequiresPostgisFact]
    public async Task Concurrent_holds_cannot_overdraw_a_wallet()
    {
        // The one that matters. Ten simultaneous holds of 100 against a balance of 500: at most
        // five can be afforded, and a lost update here would let the sixth through and leave the
        // wallet negative — the invariant the database itself also refuses.
        var userId = await AddFundedUserAsync(500m);

        var attempts = Enumerable.Range(0, 10).Select(async i =>
        {
            var (db, wallets) = NewScope();
            await using var _ = db;

            try
            {
                await wallets.PlaceHoldAsync(userId, Guid.NewGuid(), 100m, $"over:{userId}:{i}");
                return true;
            }
            catch (InsufficientCreditsException)
            {
                // The correct way to lose: told plainly there was not enough.
                return false;
            }
        });

        var results = await Task.WhenAll(attempts);

        results.Count(succeeded => succeeded).Should().Be(5);

        var (check, checkWallets) = NewScope();
        await using var __ = check;

        var wallet = await checkWallets.GetOrCreateWalletAsync(userId);

        wallet.SpendableBalance.Should().Be(0m);
        wallet.HeldBalance.Should().Be(500m);
    }

    [RequiresPostgisFact]
    public async Task A_wallet_under_contention_still_reconciles_against_its_ledger()
    {
        // Balances are a cached projection of the entries. Contention is exactly where the two
        // could drift apart, and a drift is an incident rather than a bug.
        var userId = await AddFundedUserAsync(1_000m);

        var work = Enumerable.Range(0, 15).Select(async i =>
        {
            var (db, wallets) = NewScope();
            await using var _ = db;

            var bookingId = Guid.NewGuid();
            await wallets.PlaceHoldAsync(userId, bookingId, 20m, $"mix-hold:{userId}:{i}");
            await wallets.ReleaseHoldAsync(userId, bookingId, 20m, $"mix-release:{userId}:{i}");
        });

        await Task.WhenAll(work);

        var (check, checkWallets) = NewScope();
        await using var __ = check;

        var wallet = await checkWallets.GetOrCreateWalletAsync(userId);
        var ledger = new LedgerService(check, new SystemClock());
        var replayed = await ledger.RecomputeFromEntriesAsync(wallet.Id);

        replayed.Spendable.Should().Be(wallet.SpendableBalance);
        replayed.Held.Should().Be(wallet.HeldBalance);
        replayed.Earning.Should().Be(wallet.EarningBalance);

        wallet.SpendableBalance.Should().Be(1_000m, "every hold was released again");
        wallet.HeldBalance.Should().Be(0m);
    }

    [RequiresPostgisFact]
    public async Task The_same_operation_racing_itself_is_applied_once()
    {
        // A client retrying a request it never saw the answer to, arriving twice at once. The
        // idempotency key is what makes that safe, and under contention it has to hold even when
        // both attempts read before either wrote.
        var userId = await AddFundedUserAsync(500m);
        var bookingId = Guid.NewGuid();

        var duplicates = Enumerable.Range(0, 8).Select(async _ =>
        {
            var (db, wallets) = NewScope();
            await using var __ = db;

            await wallets.PlaceHoldAsync(userId, bookingId, 50m, $"once:{bookingId}");
        });

        await Task.WhenAll(duplicates);

        var (check, checkWallets) = NewScope();
        await using var ___ = check;

        var wallet = await checkWallets.GetOrCreateWalletAsync(userId);

        wallet.HeldBalance.Should().Be(50m, "eight identical requests are still one hold");
        wallet.SpendableBalance.Should().Be(450m);
    }

    [RequiresPostgisFact]
    public async Task Two_wallets_settling_into_one_host_do_not_lose_each_other()
    {
        // The realistic contention: several sessions ending at once against the same host. Each
        // settlement reads the host's earning balance, and one overwriting the other would quietly
        // pay the host less than they earned.
        var hostId = await AddFundedUserAsync(1m);
        var renters = new List<Guid>();

        for (var i = 0; i < 6; i++)
        {
            renters.Add(await AddFundedUserAsync(200m));
        }

        var settlements = renters.Select(async renterId =>
        {
            var (db, wallets) = NewScope();
            await using var _ = db;

            var bookingId = Guid.NewGuid();
            await wallets.PlaceHoldAsync(renterId, bookingId, 100m, $"settle-hold:{bookingId}");
            await wallets.SettleAsync(new SettlementRequest(
                bookingId, renterId, hostId, 100m, $"settle:{bookingId}"));
        });

        await Task.WhenAll(settlements);

        var (check, checkWallets) = NewScope();
        await using var __ = check;

        var host = await checkWallets.GetOrCreateWalletAsync(hostId);

        // Six settlements of 100, less 10% commission each.
        host.EarningBalance.Should().Be(540m);
    }
}
