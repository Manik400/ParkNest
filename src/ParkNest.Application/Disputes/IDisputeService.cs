namespace ParkNest.Application.Disputes;

/// <summary>
/// The complaints channel for a booking that went wrong: the space was blocked, the renter never
/// turned up, the overstay charge is contested.
///
/// Nothing here edits a past transaction. A dispute that goes the complainant's way is settled by
/// posting a new compensating movement, so the ledger still replays to the balances on record and
/// the original charge stays visible in both parties' history (see docs/ledger-model.md).
/// </summary>
public interface IDisputeService
{
    /// <summary>Raises a dispute against a booking. Either party to it may.</summary>
    Task<DisputeView> RaiseAsync(RaiseDisputeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Disputes the caller raised or is named in. Admins see everything.</summary>
    Task<IReadOnlyList<DisputeView>> ListAsync(DisputeQuery query, CancellationToken cancellationToken = default);

    /// <summary>One dispute, for a party to it or an admin.</summary>
    Task<DisputeView> GetAsync(Guid disputeId, CancellationToken cancellationToken = default);

    /// <summary>Admin: acknowledges the dispute so the parties can see it is being looked at.</summary>
    Task<DisputeView> StartReviewAsync(Guid disputeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin: upholds the dispute, optionally moving credits. The adjustment is a fresh
    /// double-entry transaction, never an edit to the booking's original settlement.
    /// </summary>
    Task<DisputeView> ResolveAsync(ResolveDisputeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Admin: rejects the dispute. No money moves; the reason is recorded for the parties.</summary>
    Task<DisputeView> RejectAsync(Guid disputeId, string resolution, CancellationToken cancellationToken = default);
}

public sealed record RaiseDisputeRequest(Guid BookingId, string Reason, IReadOnlyList<DisputeEvidenceInput>? Evidence = null);

public sealed record DisputeEvidenceInput(string Url, string? Note);

/// <param name="OnlyOpen">Narrows to disputes still awaiting a decision — the admin work queue.</param>
public sealed record DisputeQuery(bool OnlyOpen = false, int Limit = 50, int Offset = 0);

/// <param name="RefundToRenter">
/// Credits to return to the renter's spendable balance. Zero settles the dispute in the
/// complainant's favour without a payment — an apology and a note on the record.
/// </param>
/// <param name="ChargedToPlatform">
/// True when the platform absorbs the refund out of its commission revenue; false when the host
/// pays it back out of their earnings. This is the substance of the decision, so it is an explicit
/// argument rather than something inferred from who complained.
/// </param>
public sealed record ResolveDisputeRequest(
    Guid DisputeId,
    string Resolution,
    decimal RefundToRenter,
    bool ChargedToPlatform);

public sealed record DisputeView(
    Guid DisputeId,
    Guid BookingId,
    Guid RaisedByUserId,
    Guid RenterId,
    Guid HostId,
    string Reason,
    string Status,
    string? Resolution,
    decimal? AdjustmentAmount,
    Guid? AdjustmentTransactionId,
    IReadOnlyList<DisputeEvidenceView> Evidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record DisputeEvidenceView(Guid Id, string Url, string? Note);
