namespace ParkNest.Domain.Common;

/// <summary>A business-rule violation. Surfaced to callers as HTTP 400/409, never as a 500.</summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}

/// <summary>The renter does not hold enough credits for the requested movement.</summary>
public sealed class InsufficientCreditsException : DomainException
{
    public InsufficientCreditsException(decimal required, decimal available)
        : base($"Insufficient credits: {required:0.00} required, {available:0.00} available.")
    {
        Required = required;
        Available = available;
    }

    public decimal Required { get; }
    public decimal Available { get; }
}

/// <summary>The debits and credits of a proposed ledger transaction do not sum to zero.</summary>
public sealed class UnbalancedLedgerTransactionException : DomainException
{
    public UnbalancedLedgerTransactionException(decimal debits, decimal credits)
        : base($"Unbalanced ledger transaction: debits {debits:0.00} != credits {credits:0.00}.") { }
}
