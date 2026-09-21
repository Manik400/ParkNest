using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Abstractions;
using ParkNest.Domain.Common;
using ParkNest.Domain.Wallets;

namespace ParkNest.Application.Wallets;

/// <summary>
/// The only component permitted to change a wallet balance. Every movement is written as a
/// balanced, append-only double-entry transaction (PRD §5.1.7), so the ledger can always be
/// replayed to reproduce current balances.
/// </summary>
public sealed class LedgerService : ILedgerService
{
    private readonly IParkNestDbContext _db;
    private readonly IClock _clock;

    public LedgerService(IParkNestDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// How many times a posting will re-read and re-apply after losing a concurrency race.
    ///
    /// Two renters settling against the same host, or one renter with two sessions ending
    /// together, both write to a wallet at once — the loser's <c>Version</c> check fails and its
    /// save is rejected. Without a retry that surfaced as an unhandled error on a booking that
    /// should simply have succeeded a moment later.
    /// </summary>
    private const int MaxConcurrencyAttempts = 5;

    public async Task<LedgerTransaction> PostAsync(
        LedgerTransactionType type,
        string idempotencyKey,
        IReadOnlyList<LedgerPosting> postings,
        Guid? bookingId = null,
        string? description = null,
        Guid? revertsTransactionId = null,
        CancellationToken cancellationToken = default)
    {
        // Whoever owns the transaction owns the retry.
        //
        // Under an ambient transaction — a caller that wrapped several writes to make them one
        // unit — retrying here is not merely useless but harmful: Postgres marks a transaction
        // aborted after a failed statement, so every command that follows fails too, including the
        // query this method would use to recover. The owner rolls back and repeats the whole unit
        // instead, which is the only correct place to decide what "again" means.
        if (_db.HasActiveTransaction)
        {
            return await TryPostAsync(
                type, idempotencyKey, postings, bookingId, description, revertsTransactionId, new List<object>(), cancellationToken);
        }

        for (var attempt = 1; ; attempt++)
        {
            // Local to the attempt, so nothing about retrying depends on this service being
            // scoped to one request at a time.
            var tracked = new List<object>();

            try
            {
                return await TryPostAsync(
                    type, idempotencyKey, postings, bookingId, description, revertsTransactionId, tracked, cancellationToken);
            }
            catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
            {
                // Almost certainly the unique index on IdempotencyKey: two copies of the same
                // request raced, both found nothing on the pre-check, and one of them lost the
                // insert. The pre-check is only an optimisation — this index is the actual
                // guarantee, and it just did its job.
                //
                // Asked rather than assumed by inspecting a provider error code: if a transaction
                // with this key now exists, the caller's work is done and that is the answer they
                // wanted. Anything else is a real failure and is rethrown.
                foreach (var entity in tracked)
                {
                    _db.Detach(entity);
                }

                var winner = await _db.LedgerTransactions
                    .Include(t => t.Entries)
                    .FirstOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey, cancellationToken);

                if (winner is not null)
                {
                    return winner;
                }

                throw;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyAttempts)
            {
                // Someone else moved this wallet between our read and our write. Nothing was
                // written — the whole save is rejected as one unit — so re-reading and re-applying
                // is safe, and the idempotency check at the top of the next attempt catches the
                // case where the winner happened to be writing this very transaction.
                //
                // Dropping what this attempt tracked is the part that matters: those copies hold
                // the values that lost, and a re-query would hand the same stale instances back.
                foreach (var entity in tracked)
                {
                    _db.Detach(entity);
                }

                // A brief, growing pause. Retrying instantly just reproduces the same collision
                // when several writers are contending for the same row.
                await Task.Delay(TimeSpan.FromMilliseconds(10 * attempt), cancellationToken);
            }
        }
    }

    private async Task<LedgerTransaction> TryPostAsync(
        LedgerTransactionType type,
        string idempotencyKey,
        IReadOnlyList<LedgerPosting> postings,
        Guid? bookingId,
        string? description,
        Guid? revertsTransactionId,
        List<object> tracked,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new DomainException("An idempotency key is required for every ledger transaction.");
        }

        var existing = await _db.LedgerTransactions
            .Include(t => t.Entries)
            .FirstOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        if (postings.Count < 2)
        {
            throw new DomainException("A double-entry transaction needs at least two legs.");
        }

        var normalised = postings
            .Select(p => p with { Amount = Money.Round(p.Amount) })
            .ToList();

        if (normalised.Any(p => p.Amount <= 0m))
        {
            throw new DomainException("Ledger amounts must be positive; use Direction to express the sign.");
        }

        var debits = Money.Round(normalised.Where(p => p.Direction == EntryDirection.Debit).Sum(p => p.Amount));
        var credits = Money.Round(normalised.Where(p => p.Direction == EntryDirection.Credit).Sum(p => p.Amount));
        if (debits != credits)
        {
            throw new UnbalancedLedgerTransactionException(debits, credits);
        }

        var now = _clock.UtcNow;
        var transaction = new LedgerTransaction
        {
            Reference = LedgerReference.New(),
            Type = type,
            IdempotencyKey = idempotencyKey,
            BookingId = bookingId,
            Description = description,
            RevertsTransactionId = revertsTransactionId,
            CreatedAt = now
        };

        // Group by wallet so each wallet is loaded once and its Version bumps once per bucket move.
        var walletIds = normalised
            .Where(p => p.WalletId.HasValue)
            .Select(p => p.WalletId!.Value)
            .Distinct()
            .ToList();

        // One transaction covering the lock and the write. The lock only holds for as long as the
        // transaction does, so acquiring it outside would protect nothing.
        var owned = _db.HasActiveTransaction ? null : await _db.BeginTransactionAsync(cancellationToken);

        try
        {
            await _db.LockWalletsAsync(walletIds, cancellationToken);

            // Read after the lock, never before: values fetched first are exactly the stale ones
            // the lock exists to stop us writing back.
            var wallets = await _db.Wallets
                .Where(w => walletIds.Contains(w.Id))
                .ToDictionaryAsync(w => w.Id, cancellationToken);

            // Noted so a lost race can drop them and read again.
            tracked.AddRange(wallets.Values);
            tracked.Add(transaction);

            foreach (var posting in normalised)
            {
                var entry = new LedgerEntry
                {
                    LedgerTransactionId = transaction.Id,
                    WalletId = posting.WalletId,
                    AccountType = posting.AccountType,
                    Direction = posting.Direction,
                    Amount = posting.Amount,
                    CreatedAt = now
                };

                transaction.Entries.Add(entry);
                tracked.Add(entry);

                if (!posting.WalletId.HasValue)
                {
                    // System accounts have no stored balance; their position is derived from entries.
                    continue;
                }

                if (!wallets.TryGetValue(posting.WalletId.Value, out var wallet))
                {
                    throw new DomainException($"Wallet {posting.WalletId} does not exist.");
                }

                wallet.Apply(posting.AccountType, posting.SignedAmountFor());
            }

            _db.LedgerTransactions.Add(transaction);
            await _db.SaveChangesAsync(cancellationToken);

            if (owned is not null)
            {
                await owned.CommitAsync(cancellationToken);
            }

            return transaction;
        }
        finally
        {
            // Disposing rolls back anything uncommitted, which is what should happen when the
            // renter turns out to be short and Apply throws part-way through.
            if (owned is not null)
            {
                await owned.DisposeAsync();
            }
        }
    }

    public async Task<WalletBalances> RecomputeFromEntriesAsync(Guid walletId, CancellationToken cancellationToken = default)
    {
        var entries = await _db.LedgerEntries
            .Where(e => e.WalletId == walletId)
            .Select(e => new { e.AccountType, e.Direction, e.Amount })
            .ToListAsync(cancellationToken);

        decimal SumFor(LedgerAccountType account) => Money.Round(entries
            .Where(e => e.AccountType == account)
            .Sum(e => e.Direction == EntryDirection.Credit ? e.Amount : -e.Amount));

        return new WalletBalances(
            SumFor(LedgerAccountType.Spendable),
            SumFor(LedgerAccountType.Held),
            SumFor(LedgerAccountType.Earning));
    }
}

internal static class LedgerPostingExtensions
{
    public static decimal SignedAmountFor(this LedgerPosting posting) =>
        posting.Direction == EntryDirection.Credit ? posting.Amount : -posting.Amount;
}
