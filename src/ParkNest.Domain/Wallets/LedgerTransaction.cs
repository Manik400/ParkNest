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

    /// <summary>
    /// The id a person quotes: <c>TXN-7K3F9QM2XA</c>. Printed on receipts and shown beside every
    /// wallet line, because a GUID is not something anyone reads out to support. Unique, minted
    /// once, and never the key anything joins on — that stays <see cref="Id"/>.
    /// </summary>
    public string Reference { get; set; } = string.Empty;

    public LedgerTransactionType Type { get; set; }

    /// <summary>
    /// The earlier transaction this one undoes, when it is a reversal: a hold released, a payout
    /// refunded, a settlement adjusted after a dispute. The ledger never edits a row, so "this
    /// was reverted" is expressed as a second row that points back at the first.
    /// </summary>
    public Guid? RevertsTransactionId { get; set; }

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

public static class LedgerReference
{
    /// <summary>
    /// No 0/O/1/I: the reference is read aloud and typed into a support form, and those four are
    /// the ones people confuse.
    /// </summary>
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public const string Prefix = "TXN-";
    public const int Length = 14;

    /// <summary>
    /// Ten characters from a 32-letter alphabet: fifty bits of randomness, so a collision is not
    /// a practical concern and the unique index is the guarantee rather than the expectation.
    /// </summary>
    public static string New()
    {
        var bytes = new byte[10];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);

        var chars = new char[10];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        }

        return Prefix + new string(chars);
    }
}
