using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;
using ParkNest.Domain.Payouts;
using ParkNest.Domain.Wallets;

namespace ParkNest.Application.Wallets;

/// <summary>
/// Credit operations expressed in product terms. All actual balance movement delegates to
/// <see cref="ILedgerService"/> — this class only decides <em>which</em> postings to make.
/// </summary>
public sealed class WalletService : IWalletService
{
    private readonly IParkNestDbContext _db;
    private readonly ILedgerService _ledger;
    private readonly IClock _clock;
    private readonly PlatformOptions _options;

    public WalletService(
        IParkNestDbContext db,
        ILedgerService ledger,
        IClock clock,
        IOptions<PlatformOptions> options)
    {
        _db = db;
        _ledger = ledger;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<Wallet> GetOrCreateWalletAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var wallet = await _db.Wallets.FirstOrDefaultAsync(w => w.UserId == userId, cancellationToken);
        if (wallet is not null)
        {
            return wallet;
        }

        wallet = new Wallet { UserId = userId, CreatedAt = _clock.UtcNow };
        _db.Wallets.Add(wallet);
        await _db.SaveChangesAsync(cancellationToken);
        return wallet;
    }

    public async Task<Wallet> RechargeAsync(Guid userId, decimal amount, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        amount = Money.Round(amount);
        if (amount < _options.MinimumRechargeCredits)
        {
            throw new DomainException($"Minimum recharge is {_options.MinimumRechargeCredits:0.00} credits.");
        }

        var wallet = await GetOrCreateWalletAsync(userId, cancellationToken);

        await _ledger.PostAsync(
            LedgerTransactionType.Recharge,
            idempotencyKey,
            new[]
            {
                LedgerPosting.Debit(null, LedgerAccountType.ExternalFunding, amount),
                LedgerPosting.Credit(wallet.Id, LedgerAccountType.Spendable, amount)
            },
            description: $"Recharge {amount:0.00} credits",
            cancellationToken: cancellationToken);

        return wallet;
    }

    public async Task<Wallet> PlaceHoldAsync(Guid userId, Guid bookingId, decimal amount, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        amount = Money.Round(amount);
        var wallet = await GetOrCreateWalletAsync(userId, cancellationToken);

        if (wallet.SpendableBalance < amount)
        {
            throw new InsufficientCreditsException(amount, wallet.SpendableBalance);
        }

        await _ledger.PostAsync(
            LedgerTransactionType.Hold,
            idempotencyKey,
            new[]
            {
                LedgerPosting.Debit(wallet.Id, LedgerAccountType.Spendable, amount),
                LedgerPosting.Credit(wallet.Id, LedgerAccountType.Held, amount)
            },
            bookingId,
            $"Hold {amount:0.00} credits for booking {bookingId}",
            cancellationToken);

        return wallet;
    }

    public async Task<Wallet> ReleaseHoldAsync(Guid userId, Guid bookingId, decimal amount, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        amount = Money.Round(amount);
        var wallet = await GetOrCreateWalletAsync(userId, cancellationToken);

        if (amount <= 0m)
        {
            return wallet;
        }

        await _ledger.PostAsync(
            LedgerTransactionType.ReleaseHold,
            idempotencyKey,
            new[]
            {
                LedgerPosting.Debit(wallet.Id, LedgerAccountType.Held, amount),
                LedgerPosting.Credit(wallet.Id, LedgerAccountType.Spendable, amount)
            },
            bookingId,
            $"Release {amount:0.00} unused credits for booking {bookingId}",
            cancellationToken);

        return wallet;
    }

    public async Task<OverstayDebitResult> DebitOverstayAsync(Guid userId, Guid bookingId, decimal amount, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        amount = Money.Round(amount);
        if (amount <= 0m)
        {
            return new OverstayDebitResult(0m, 0m);
        }

        var wallet = await GetOrCreateWalletAsync(userId, cancellationToken);

        // Take whatever the renter has rather than failing outright: partial coverage still moves
        // real value to the host, and the remainder becomes a tracked shortfall (PRD §5.1.4).
        var covered = Math.Min(amount, wallet.SpendableBalance);
        var shortfall = Money.Round(amount - covered);

        if (covered > 0m)
        {
            await _ledger.PostAsync(
                LedgerTransactionType.OverstayDebit,
                idempotencyKey,
                new[]
                {
                    LedgerPosting.Debit(wallet.Id, LedgerAccountType.Spendable, covered),
                    LedgerPosting.Credit(wallet.Id, LedgerAccountType.Held, covered)
                },
                bookingId,
                $"Overstay debit {covered:0.00} credits for booking {bookingId}",
                cancellationToken);
        }

        return new OverstayDebitResult(covered, shortfall);
    }

    public async Task<SettlementResult> SettleAsync(SettlementRequest request, CancellationToken cancellationToken = default)
    {
        var gross = Money.Round(request.AmountFromHold);
        if (gross <= 0m)
        {
            return new SettlementResult(0m, 0m, 0m);
        }

        var renterWallet = await GetOrCreateWalletAsync(request.RenterId, cancellationToken);
        var hostWallet = await GetOrCreateWalletAsync(request.HostId, cancellationToken);

        var fee = Money.Round(gross * _options.CommissionRate);
        var hostShare = Money.Round(gross - fee);

        var postings = new List<LedgerPosting>
        {
            LedgerPosting.Debit(renterWallet.Id, LedgerAccountType.Held, gross),
            LedgerPosting.Credit(hostWallet.Id, LedgerAccountType.Earning, hostShare)
        };

        if (fee > 0m)
        {
            postings.Add(LedgerPosting.Credit(null, LedgerAccountType.PlatformRevenue, fee));
        }

        await _ledger.PostAsync(
            LedgerTransactionType.Settlement,
            request.IdempotencyKey,
            postings,
            request.BookingId,
            $"Settle booking {request.BookingId}: {hostShare:0.00} to host, {fee:0.00} commission",
            cancellationToken);

        return new SettlementResult(gross, fee, hostShare);
    }

    public async Task<Payout> RequestCashOutAsync(Guid hostId, decimal amount, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        amount = Money.Round(amount);

        var host = await _db.Users.FirstOrDefaultAsync(u => u.Id == hostId, cancellationToken)
                   ?? throw new DomainException($"User {hostId} does not exist.");

        if (host.KycStatus != KycStatus.Verified)
        {
            // Said in terms of what to do about it. "KYC must be verified" is a status; the host
            // needs to know there is a screen where they can fix it.
            throw new DomainException(
                host.KycStatus == KycStatus.Pending
                    ? "Your identity check is still being reviewed. Cash-out opens as soon as it clears."
                    : "Verify your identity before cashing out.");
        }

        // The trust score gates this and nothing else, which is the point: money leaving the
        // platform is the movement that cannot be undone cheaply, and a host with a sustained
        // record of violations is exactly who should be talked to before it does.
        if (_options.MinimumTrustScoreForCashOut > 0
            && host.TrustScore < _options.MinimumTrustScoreForCashOut)
        {
            throw new DomainException(
                "Your account is under review after repeated session problems. Contact support to release earnings.");
        }

        var wallet = await GetOrCreateWalletAsync(hostId, cancellationToken);

        if (amount < _options.MinimumCashOutCredits)
        {
            throw new DomainException($"Minimum cash-out is {_options.MinimumCashOutCredits:0.00} credits.");
        }

        if (wallet.EarningBalance < amount)
        {
            throw new InsufficientCreditsException(amount, wallet.EarningBalance);
        }

        var payout = new Payout
        {
            HostId = hostId,
            Amount = amount,
            Status = PayoutStatus.Requested,
            CreatedAt = _clock.UtcNow
        };

        // The ledger debit happens now so the credits cannot be double-spent while the payout is in
        // flight. A failed payout is corrected by a compensating Refund transaction, never a delete.
        await _ledger.PostAsync(
            LedgerTransactionType.Payout,
            idempotencyKey,
            new[]
            {
                LedgerPosting.Debit(wallet.Id, LedgerAccountType.Earning, amount),
                LedgerPosting.Credit(null, LedgerAccountType.ExternalPayout, amount)
            },
            description: $"Payout {amount:0.00} credits to host {hostId}",
            cancellationToken: cancellationToken);

        _db.Payouts.Add(payout);
        await _db.SaveChangesAsync(cancellationToken);

        return payout;
    }

    public async Task<Payout> CompletePayoutAsync(
        Guid payoutId,
        string? providerReference,
        CancellationToken cancellationToken = default)
    {
        var payout = await FindPayoutAsync(payoutId, cancellationToken);

        if (payout.Status == PayoutStatus.Paid)
        {
            // Aggregators retry their callbacks, so seeing this twice is normal.
            return payout;
        }

        if (payout.Status == PayoutStatus.Failed)
        {
            // The refund has already been posted and the credits are back in the host's earning
            // balance. Flipping the status to Paid now would claim money left the system when the
            // ledger says it came back, and there would be no debit to point at.
            throw new DomainException(
                $"Payout {payoutId} was already refunded as failed. Request a new cash-out instead.");
        }

        payout.Status = PayoutStatus.Paid;
        payout.ProviderReference = providerReference;
        payout.FailureReason = null;
        payout.CompletedAt = _clock.UtcNow;

        // No ledger posting: the debit went in when the payout was requested, precisely so the
        // credits could not be spent while it was in flight. Success is the expected end of that
        // movement, not a new one.
        await _db.SaveChangesAsync(cancellationToken);

        return payout;
    }

    public async Task<Payout> FailPayoutAsync(
        Guid payoutId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var payout = await FindPayoutAsync(payoutId, cancellationToken);

        if (payout.Status == PayoutStatus.Failed)
        {
            return payout;
        }

        if (payout.Status == PayoutStatus.Paid)
        {
            throw new DomainException(
                $"Payout {payoutId} is already paid and cannot be failed. A reversal after settlement is a dispute, not a payout failure.");
        }

        var wallet = await GetOrCreateWalletAsync(payout.HostId, cancellationToken);

        // A compensating transaction, not a deletion: the ledger is append-only and the failed
        // attempt is part of the host's history. The pair reads as "money left, money came back",
        // which is what actually happened.
        await _ledger.PostAsync(
            LedgerTransactionType.Refund,
            $"payout-refund:{payout.Id}",
            new[]
            {
                LedgerPosting.Debit(null, LedgerAccountType.ExternalPayout, payout.Amount),
                LedgerPosting.Credit(wallet.Id, LedgerAccountType.Earning, payout.Amount)
            },
            description: $"Refund failed payout {payout.Id}: {reason}",
            cancellationToken: cancellationToken);

        payout.Status = PayoutStatus.Failed;
        payout.FailureReason = reason;
        payout.CompletedAt = _clock.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return payout;
    }

    private async Task<Payout> FindPayoutAsync(Guid payoutId, CancellationToken cancellationToken) =>
        await _db.Payouts.FirstOrDefaultAsync(p => p.Id == payoutId, cancellationToken)
        ?? throw new DomainException($"Payout {payoutId} does not exist.");
}
