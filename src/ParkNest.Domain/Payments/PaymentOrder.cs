namespace ParkNest.Domain.Payments;

public enum PaymentOrderStatus
{
    /// <summary>Created with the gateway, waiting for the user to pay.</summary>
    Created = 0,

    /// <summary>Payment confirmed by a verified webhook and credits issued.</summary>
    Paid = 1,

    Failed = 2,

    /// <summary>Abandoned or expired without payment.</summary>
    Cancelled = 3
}

/// <summary>
/// A intent to buy credits. Created before the user is sent to the gateway, and only turned into
/// credits when a signed webhook confirms the money actually arrived.
///
/// This record is what makes recharge trustworthy: the amount is fixed here, server-side, so a
/// tampered callback claiming a larger figure has nothing to stand on (see ADR 0004).
/// </summary>
public class PaymentOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>Credits to issue on success. Set at creation and never taken from the callback.</summary>
    public decimal Amount { get; set; }

    public string Currency { get; set; } = "INR";

    /// <summary>The gateway's own order id, which the webhook refers back to.</summary>
    public string ProviderOrderId { get; set; } = string.Empty;

    /// <summary>The gateway's payment id, once one exists.</summary>
    public string? ProviderPaymentId { get; set; }

    public PaymentOrderStatus Status { get; set; } = PaymentOrderStatus.Created;

    public string? FailureReason { get; set; }

    /// <summary>Ledger transaction that issued the credits, once paid.</summary>
    public Guid? LedgerTransactionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsSettled => Status is PaymentOrderStatus.Paid or PaymentOrderStatus.Failed;
}
