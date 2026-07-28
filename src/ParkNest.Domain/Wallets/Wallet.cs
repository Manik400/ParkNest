using ParkNest.Domain.Common;

namespace ParkNest.Domain.Wallets;

/// <summary>
/// A user's three credit buckets. These columns are a cached projection of the ledger —
/// the append-only <see cref="LedgerEntry"/> stream is the source of truth, and
/// <c>SELECT sum(entries)</c> must always reproduce these numbers.
/// </summary>
public class Wallet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    /// <summary>Credits free to spend on a new booking.</summary>
    public decimal SpendableBalance { get; set; }

    /// <summary>Credits reserved against open bookings. Not spendable, not yet the host's.</summary>
    public decimal HeldBalance { get; set; }

    /// <summary>Credits earned as a host, awaiting cash-out.</summary>
    public decimal EarningBalance { get; set; }

    /// <summary>Optimistic-concurrency token, so two concurrent debits can't both read a stale balance.</summary>
    public int Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public decimal GetBalance(LedgerAccountType accountType) => accountType switch
    {
        LedgerAccountType.Spendable => SpendableBalance,
        LedgerAccountType.Held => HeldBalance,
        LedgerAccountType.Earning => EarningBalance,
        _ => throw new DomainException($"{accountType} is a system account and has no wallet balance.")
    };

    /// <summary>Applies a signed delta to one bucket. Rejects any movement that would go negative.</summary>
    public void Apply(LedgerAccountType accountType, decimal signedDelta)
    {
        var next = Money.Round(GetBalance(accountType) + signedDelta);
        if (next < 0m)
        {
            throw new InsufficientCreditsException(Math.Abs(signedDelta), GetBalance(accountType));
        }

        switch (accountType)
        {
            case LedgerAccountType.Spendable:
                SpendableBalance = next;
                break;
            case LedgerAccountType.Held:
                HeldBalance = next;
                break;
            case LedgerAccountType.Earning:
                EarningBalance = next;
                break;
            default:
                throw new DomainException($"{accountType} is a system account and has no wallet balance.");
        }

        Version++;
    }
}
