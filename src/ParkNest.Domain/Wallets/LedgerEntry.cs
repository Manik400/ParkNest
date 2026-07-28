using ParkNest.Domain.Common;

namespace ParkNest.Domain.Wallets;

/// <summary>One leg of a <see cref="LedgerTransaction"/>. Immutable once written.</summary>
public class LedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LedgerTransactionId { get; set; }
    public LedgerTransaction? Transaction { get; set; }

    /// <summary>Null for platform system accounts (revenue, external funding, external payout).</summary>
    public Guid? WalletId { get; set; }

    public LedgerAccountType AccountType { get; set; }
    public EntryDirection Direction { get; set; }

    /// <summary>Always positive; <see cref="Direction"/> carries the sign.</summary>
    public decimal Amount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>+amount when value enters the account, -amount when it leaves.</summary>
    public decimal SignedAmount => Direction == EntryDirection.Credit ? Amount : -Amount;

    public bool IsSystemAccount => AccountType is
        LedgerAccountType.PlatformRevenue or
        LedgerAccountType.ExternalFunding or
        LedgerAccountType.ExternalPayout;
}
