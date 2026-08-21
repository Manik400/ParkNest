using ParkNest.Domain.Common;

namespace ParkNest.Domain.Users;

/// <summary>
/// One attempt by a host to prove who they are.
///
/// A row per attempt rather than a set of columns on the user, because a rejection has to survive
/// the resubmission that follows it: "this is the third time this account has sent a document that
/// does not match the name" is exactly the thing an operator needs to see, and a single mutable
/// record would erase it.
///
/// What is deliberately *not* stored is the document number. The last four digits are enough for a
/// human to match a number against the photo in front of them, and a keyed hash is enough to spot
/// the same document arriving under two accounts. Keeping the whole number would mean holding
/// identity documents for every host in a database that does not need them to do its job.
/// </summary>
public class KycSubmission
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>The name as it appears on the document, which is what the operator compares.</summary>
    public string LegalName { get; set; } = string.Empty;

    public KycDocumentType DocumentType { get; set; }

    /// <summary>The last four characters of the document number. Never the whole of it.</summary>
    public string DocumentLast4 { get; set; } = string.Empty;

    /// <summary>
    /// Keyed hash of the full number, so the same document turning up under a second account is
    /// visible without the number itself being recoverable from this table.
    /// </summary>
    public string DocumentHash { get; set; } = string.Empty;

    /// <summary>Where the photograph of the document is stored. Null when storage is unconfigured.</summary>
    public string? DocumentPhotoUrl { get; set; }

    /// <summary>Last four of the bank account the payout will go to, for the operator to sanity-check.</summary>
    public string? PayoutAccountLast4 { get; set; }

    public KycStatus Status { get; set; } = KycStatus.Pending;

    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>Which operator decided. Money leaves the platform on the back of this decision.</summary>
    public Guid? ReviewedByUserId { get; set; }

    /// <summary>Why it was refused. The host reads this, so it has to say something actionable.</summary>
    public string? RejectionReason { get; set; }
}
