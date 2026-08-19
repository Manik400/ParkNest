using ParkNest.Domain.Wallets;

namespace ParkNest.Application.Wallets;

public interface IWalletService
{
    Task<Wallet> GetOrCreateWalletAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Real money entering the system. Credits spendable balance against external funding.</summary>
    Task<Wallet> RechargeAsync(Guid userId, decimal amount, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Moves spendable → held. Throws <c>InsufficientCreditsException</c> if the renter is short.</summary>
    Task<Wallet> PlaceHoldAsync(Guid userId, Guid bookingId, decimal amount, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Moves held → spendable, for the unused part of a booking or a cancellation.</summary>
    Task<Wallet> ReleaseHoldAsync(Guid userId, Guid bookingId, decimal amount, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pulls extra credits spendable → held to cover time past the booked window. Returns how much
    /// it could actually cover — the caller decides whether the shortfall is a violation yet.
    /// </summary>
    Task<OverstayDebitResult> DebitOverstayAsync(Guid userId, Guid bookingId, decimal amount, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Final movement of a session: renter held → host earning, less platform commission.
    /// </summary>
    Task<SettlementResult> SettleAsync(SettlementRequest request, CancellationToken cancellationToken = default);

    /// <summary>Host earning → external payout. Enforces the cash-out floor and KYC.</summary>
    Task<Domain.Payouts.Payout> RequestCashOutAsync(Guid hostId, decimal amount, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// The aggregator has moved the money. Closes the payout; the ledger already recorded the
    /// debit when it was requested, so nothing new is posted.
    /// </summary>
    Task<Domain.Payouts.Payout> CompletePayoutAsync(Guid payoutId, string? providerReference, CancellationToken cancellationToken = default);

    /// <summary>
    /// The aggregator rejected the payout. Posts a compensating Refund that returns the credits to
    /// the host's earning balance — the original debit is never deleted or edited.
    /// </summary>
    Task<Domain.Payouts.Payout> FailPayoutAsync(Guid payoutId, string reason, CancellationToken cancellationToken = default);
}

public readonly record struct OverstayDebitResult(decimal Covered, decimal Shortfall)
{
    public bool FullyCovered => Shortfall <= 0m;
}

public sealed record SettlementRequest(
    Guid BookingId,
    Guid RenterId,
    Guid HostId,
    decimal AmountFromHold,
    string IdempotencyKey);

public readonly record struct SettlementResult(decimal GrossAmount, decimal PlatformFee, decimal HostCredited);
