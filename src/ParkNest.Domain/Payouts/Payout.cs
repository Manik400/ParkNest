using ParkNest.Domain.Common;

namespace ParkNest.Domain.Payouts;

public class Payout
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HostId { get; set; }

    public decimal Amount { get; set; }

    /// <summary>Token from the payment aggregator; no bank details are stored in ParkNest (PRD §16).</summary>
    public string? BankDetailsRef { get; set; }

    /// <summary>Aggregator-side payout id, once accepted.</summary>
    public string? ProviderReference { get; set; }

    public PayoutStatus Status { get; set; } = PayoutStatus.Requested;
    public string? FailureReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}
