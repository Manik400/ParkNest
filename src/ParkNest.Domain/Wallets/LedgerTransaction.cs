using ParkNest.Domain.Common;

namespace ParkNest.Domain.Wallets;

/// <summary>
/// One balanced, append-only movement of credits. Nothing in the system mutates a wallet
/// without writing one of these, and no transaction is ever deleted or edited — a mistake is
/// corrected by posting a compensating transaction.
/// </summary>
public class LedgerTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public LedgerTransactionType Type { get; set; }

    /// <summary>
    /// Caller-supplied key, unique across the table. A retried request (flaky mobile network
    /// mid-checkout) replays to the same transaction instead of double-debiting.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public Guid? BookingId { get; set; }
    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<LedgerEntry> Entries { get; set; } = new List<LedgerEntry>();

    public decimal TotalDebits => Entries.Where(e => e.Direction == EntryDirection.Debit).Sum(e => e.Amount);
    public decimal TotalCredits => Entries.Where(e => e.Direction == EntryDirection.Credit).Sum(e => e.Amount);
    public bool IsBalanced => Money.Round(TotalDebits) == Money.Round(TotalCredits);
}
