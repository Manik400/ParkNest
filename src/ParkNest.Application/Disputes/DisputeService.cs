using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Wallets;
using ParkNest.Domain.Bookings;
using ParkNest.Domain.Common;
using ParkNest.Domain.Disputes;

namespace ParkNest.Application.Disputes;

public sealed class DisputeService : IDisputeService
{
    private readonly IParkNestDbContext _db;
    private readonly ILedgerService _ledger;
    private readonly IWalletService _wallets;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly ILogger<DisputeService> _logger;

    public DisputeService(
        IParkNestDbContext db,
        ILedgerService ledger,
        IWalletService wallets,
        ICurrentUser currentUser,
        IClock clock,
        ILogger<DisputeService> logger)
    {
        _db = db;
        _ledger = ledger;
        _wallets = wallets;
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;
    }

    public async Task<DisputeView> RaiseAsync(RaiseDisputeRequest request, CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new DomainException("A dispute needs a reason.");
        }

        var booking = await _db.Bookings.FirstOrDefaultAsync(b => b.Id == request.BookingId, cancellationToken)
                      ?? throw new DomainException($"Booking {request.BookingId} does not exist.");

        // Either side may complain, and only they may. Anyone else asking is probing for booking
        // ids, so this is a 403 rather than a "no such booking".
        if (booking.RenterId != userId && booking.HostId != userId)
        {
            throw new ForbiddenException("Only the renter or the host can dispute this booking.");
        }

        if (booking.Status is BookingStatus.Held or BookingStatus.Cancelled)
        {
            // Nothing has happened yet that a dispute could be about, and no money has moved that
            // an adjustment could compensate. Cancel it instead.
            throw new DomainException("A booking can only be disputed once the session has started.");
        }

        var alreadyOpen = await _db.Disputes.AnyAsync(
            d => d.BookingId == booking.Id
                 && (d.Status == DisputeStatus.Open || d.Status == DisputeStatus.UnderReview),
            cancellationToken);

        if (alreadyOpen)
        {
            // One queue entry per booking. Two open disputes over the same session invite two
            // adjustments for the same complaint.
            throw new DomainException("This booking already has a dispute awaiting a decision.");
        }

        var dispute = new Dispute
        {
            BookingId = booking.Id,
            RaisedByUserId = userId,
            Reason = request.Reason.Trim(),
            Status = DisputeStatus.Open,
            CreatedAt = _clock.UtcNow
        };

        foreach (var evidence in request.Evidence ?? Array.Empty<DisputeEvidenceInput>())
        {
            if (string.IsNullOrWhiteSpace(evidence.Url))
            {
                continue;
            }

            dispute.Evidence.Add(new DisputeEvidence
            {
                DisputeId = dispute.Id,
                Url = evidence.Url.Trim(),
                Note = evidence.Note
            });
        }

        _db.Disputes.Add(dispute);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Dispute {DisputeId} raised against booking {BookingId}.", dispute.Id, booking.Id);

        return await ViewAsync(dispute.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<DisputeView>> ListAsync(DisputeQuery query, CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();
        var isAdmin = _currentUser.Role == UserRole.Admin;

        var disputes =
            from dispute in _db.Disputes
            join booking in _db.Bookings on dispute.BookingId equals booking.Id
            where isAdmin || booking.RenterId == userId || booking.HostId == userId
            select new { dispute, booking };

        if (query.OnlyOpen)
        {
            disputes = disputes.Where(d =>
                d.dispute.Status == DisputeStatus.Open || d.dispute.Status == DisputeStatus.UnderReview);
        }

        var ids = await disputes
            // Oldest first: the dispute nobody has answered has been waiting longest.
            .OrderBy(d => d.dispute.CreatedAt)
            .Skip(Math.Max(0, query.Offset))
            .Take(Math.Clamp(query.Limit, 1, 200))
            .Select(d => d.dispute.Id)
            .ToListAsync(cancellationToken);

        var views = new List<DisputeView>(ids.Count);
        foreach (var id in ids)
        {
            views.Add(await ViewAsync(id, cancellationToken));
        }

        return views;
    }

    public async Task<DisputeView> GetAsync(Guid disputeId, CancellationToken cancellationToken = default)
    {
        var (dispute, booking) = await LoadAsync(disputeId, cancellationToken);
        RequireParty(booking);
        return await ViewAsync(dispute.Id, cancellationToken);
    }

    public async Task<DisputeView> StartReviewAsync(Guid disputeId, CancellationToken cancellationToken = default)
    {
        _currentUser.RequireAdmin();
        var (dispute, _) = await LoadAsync(disputeId, cancellationToken);

        RequireUndecided(dispute);

        dispute.Status = DisputeStatus.UnderReview;
        await _db.SaveChangesAsync(cancellationToken);

        return await ViewAsync(dispute.Id, cancellationToken);
    }

    public async Task<DisputeView> ResolveAsync(ResolveDisputeRequest request, CancellationToken cancellationToken = default)
    {
        _currentUser.RequireAdmin();

        if (string.IsNullOrWhiteSpace(request.Resolution))
        {
            // Both parties read this. A decision with no stated reason is not a decision.
            throw new DomainException("A resolution needs to say what was decided.");
        }

        var (dispute, booking) = await LoadAsync(request.DisputeId, cancellationToken);
        RequireUndecided(dispute);

        var refund = Money.Round(request.RefundToRenter);

        if (refund < 0m)
        {
            throw new DomainException(
                "A refund cannot be negative. To decide against the renter, reject the dispute instead.");
        }

        var collected = Money.Round(booking.SettledAmount + booking.OverstayAmount);

        if (refund > collected)
        {
            // The adjustment compensates one specific session. Letting it exceed what that session
            // ever collected turns the dispute console into an unaudited money printer.
            throw new DomainException(
                $"A refund cannot exceed what the booking collected ({collected:0.00} credits).");
        }

        if (refund > 0m)
        {
            var renterWallet = await _wallets.GetOrCreateWalletAsync(booking.RenterId, cancellationToken);

            LedgerPosting source;

            if (request.ChargedToPlatform)
            {
                // Out of commission revenue: the platform takes the loss, which is the usual
                // outcome when neither party is clearly at fault.
                source = LedgerPosting.Debit(null, LedgerAccountType.PlatformRevenue, refund);
            }
            else
            {
                // Out of the host's earnings. This fails if they have already cashed out — an
                // earning balance cannot go negative, and inventing credits to cover a clawback
                // would break the one invariant the whole system rests on. The operator is then
                // choosing between charging the platform and waiting for the host to earn again,
                // which is a real decision and should not be made silently on their behalf.
                var hostWallet = await _wallets.GetOrCreateWalletAsync(booking.HostId, cancellationToken);
                source = LedgerPosting.Debit(hostWallet.Id, LedgerAccountType.Earning, refund);
            }

            var transaction = await _ledger.PostAsync(
                LedgerTransactionType.AdminAdjustment,
                $"dispute:{dispute.Id}",
                new[]
                {
                    source,
                    LedgerPosting.Credit(renterWallet.Id, LedgerAccountType.Spendable, refund)
                },
                booking.Id,
                $"Dispute {dispute.Id} resolved: {refund:0.00} credits to the renter",
                cancellationToken);

            dispute.AdjustmentTransactionId = transaction.Id;
        }

        dispute.Status = DisputeStatus.Resolved;
        dispute.Resolution = request.Resolution.Trim();
        dispute.ResolvedAt = _clock.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Dispute {DisputeId} resolved with a {Refund} credit adjustment charged to {Payer}.",
            dispute.Id, refund, request.ChargedToPlatform ? "the platform" : "the host");

        return await ViewAsync(dispute.Id, cancellationToken);
    }

    public async Task<DisputeView> RejectAsync(Guid disputeId, string resolution, CancellationToken cancellationToken = default)
    {
        _currentUser.RequireAdmin();

        if (string.IsNullOrWhiteSpace(resolution))
        {
            throw new DomainException("A rejection needs to say why.");
        }

        var (dispute, _) = await LoadAsync(disputeId, cancellationToken);
        RequireUndecided(dispute);

        dispute.Status = DisputeStatus.Rejected;
        dispute.Resolution = resolution.Trim();
        dispute.ResolvedAt = _clock.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return await ViewAsync(dispute.Id, cancellationToken);
    }

    private async Task<(Dispute Dispute, Booking Booking)> LoadAsync(Guid disputeId, CancellationToken cancellationToken)
    {
        var dispute = await _db.Disputes
            .Include(d => d.Evidence)
            .FirstOrDefaultAsync(d => d.Id == disputeId, cancellationToken)
            ?? throw new DomainException($"Dispute {disputeId} does not exist.");

        var booking = await _db.Bookings.FirstOrDefaultAsync(b => b.Id == dispute.BookingId, cancellationToken)
                      ?? throw new DomainException($"Booking {dispute.BookingId} does not exist.");

        return (dispute, booking);
    }

    private void RequireParty(Booking booking)
    {
        var userId = _currentUser.RequireUserId();

        if (_currentUser.Role != UserRole.Admin && booking.RenterId != userId && booking.HostId != userId)
        {
            throw new ForbiddenException("This dispute is not yours.");
        }
    }

    /// <summary>
    /// A decided dispute is final. Re-resolving one would post a second adjustment for the same
    /// complaint — and because the idempotency key is derived from the dispute id, the ledger
    /// would quietly return the first transaction instead. The caller would see success while no
    /// money moved, which is the worst of both outcomes.
    /// </summary>
    private static void RequireUndecided(Dispute dispute)
    {
        if (dispute.Status is DisputeStatus.Resolved or DisputeStatus.Rejected)
        {
            throw new DomainException($"Dispute {dispute.Id} has already been decided.");
        }
    }

    private async Task<DisputeView> ViewAsync(Guid disputeId, CancellationToken cancellationToken)
    {
        var dispute = await _db.Disputes
            .Include(d => d.Evidence)
            .AsNoTracking()
            .FirstAsync(d => d.Id == disputeId, cancellationToken);

        var booking = await _db.Bookings
            .AsNoTracking()
            .FirstAsync(b => b.Id == dispute.BookingId, cancellationToken);

        // Read back off the ledger rather than cached on the dispute, so what was paid out has
        // exactly one home.
        decimal? adjustment = null;
        if (dispute.AdjustmentTransactionId is { } transactionId)
        {
            adjustment = await _db.LedgerEntries
                .Where(e => e.LedgerTransactionId == transactionId && e.Direction == EntryDirection.Credit)
                .Select(e => (decimal?)e.Amount)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return new DisputeView(
            dispute.Id,
            dispute.BookingId,
            dispute.RaisedByUserId,
            booking.RenterId,
            booking.HostId,
            dispute.Reason,
            dispute.Status.ToString(),
            dispute.Resolution,
            adjustment,
            dispute.AdjustmentTransactionId,
            dispute.Evidence.Select(e => new DisputeEvidenceView(e.Id, e.Url, e.Note)).ToList(),
            dispute.CreatedAt,
            dispute.ResolvedAt);
    }
}
