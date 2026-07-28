using ParkNest.Domain.Common;

namespace ParkNest.Application.Wallets;

/// <summary>One requested leg of a ledger transaction, before it is validated and persisted.</summary>
/// <param name="WalletId">Null for platform system accounts.</param>
public readonly record struct LedgerPosting(
    Guid? WalletId,
    LedgerAccountType AccountType,
    EntryDirection Direction,
    decimal Amount)
{
    public static LedgerPosting Debit(Guid? walletId, LedgerAccountType account, decimal amount) =>
        new(walletId, account, EntryDirection.Debit, amount);

    public static LedgerPosting Credit(Guid? walletId, LedgerAccountType account, decimal amount) =>
        new(walletId, account, EntryDirection.Credit, amount);
}
