using ParkNest.Domain.Common;

namespace ParkNest.Domain.Disputes;

public class Dispute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookingId { get; set; }
    public Guid RaisedByUserId { get; set; }

    public string Reason { get; set; } = string.Empty;
    public DisputeStatus Status { get; set; } = DisputeStatus.Open;

    public string? Resolution { get; set; }

    /// <summary>Set when resolving the dispute posted a compensating ledger transaction.</summary>
    public Guid? AdjustmentTransactionId { get; set; }

    public ICollection<DisputeEvidence> Evidence { get; set; } = new List<DisputeEvidence>();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
}

public class DisputeEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DisputeId { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? Note { get; set; }
}
