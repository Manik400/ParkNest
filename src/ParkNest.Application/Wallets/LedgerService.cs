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

    public async Task<LedgerTransaction> PostAsync(
        LedgerTransactionType type,
        string idempotencyKey,
        IReadOnlyList<LedgerPosting> postings,
        Guid? bookingId = null,
        string? description = null,
        CancellationToken cancellationToken = default)
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
            Type = type,
            IdempotencyKey = idempotencyKey,
            BookingId = bookingId,
            Description = description,
            CreatedAt = now
        };

        // Group by wallet so each wallet is loaded once and its Version bumps once per bucket move.
        var walletIds = normalised
            .Where(p => p.WalletId.HasValue)
            .Select(p => p.WalletId!.Value)
            .Distinct()
            .ToList();

        var wallets = await _db.Wallets
            .Where(w => walletIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, cancellationToken);

        foreach (var posting in normalised)
        {
            transaction.Entries.Add(new LedgerEntry
            {
                LedgerTransactionId = transaction.Id,
                WalletId = posting.WalletId,
                AccountType = posting.AccountType,
                Direction = posting.Direction,
                Amount = posting.Amount,
                CreatedAt = now
            });

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

        return transaction;
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
