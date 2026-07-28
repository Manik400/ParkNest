using ParkNest.Domain.Common;
using ParkNest.Domain.Wallets;

namespace ParkNest.Application.Wallets;

public interface ILedgerService
{
    /// <summary>
    /// Validates and persists one balanced transaction, applying each leg to the affected wallet
    /// buckets. Idempotent on <paramref name="idempotencyKey"/>: replaying a key returns the
    /// transaction that was already written, without moving credits a second time.
    /// </summary>
    Task<LedgerTransaction> PostAsync(
        LedgerTransactionType type,
        string idempotencyKey,
        IReadOnlyList<LedgerPosting> postings,
        Guid? bookingId = null,
        string? description = null,
        CancellationToken cancellationToken = default);

    /// <summary>Recomputes a wallet's buckets from its entries. Used by reconciliation jobs and tests.</summary>
    Task<WalletBalances> RecomputeFromEntriesAsync(Guid walletId, CancellationToken cancellationToken = default);
}

public readonly record struct WalletBalances(decimal Spendable, decimal Held, decimal Earning);
