using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Wallets;
using ParkNest.Domain.Common;

namespace ParkNest.Api.Controllers;

/// <summary>
/// The payout queue, as operations sees it.
///
/// Cash-out is not automatic and should not be: money leaving the system is the one movement
/// nothing else can compensate for cheaply, and until the aggregator arrangement exists somebody
/// makes each transfer by hand. These endpoints are where they record what happened — and the
/// recording is what moves the ledger, not the bank transfer itself.
/// </summary>
[ApiController]
[Route("api/admin/payouts")]
public sealed class AdminPayoutsController : ControllerBase
{
    private readonly IParkNestDbContext _db;
    private readonly IWalletService _wallets;
    private readonly ICurrentUser _currentUser;

    public AdminPayoutsController(IParkNestDbContext db, IWalletService wallets, ICurrentUser currentUser)
    {
        _db = db;
        _wallets = wallets;
        _currentUser = currentUser;
    }

    /// <summary>The queue, oldest first — a payout that has waited longest is the one to action.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PayoutView>>> List(
        [FromQuery] PayoutStatus? status,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        _currentUser.RequireAdmin();

        var query = _db.Payouts.AsQueryable();

        if (status.HasValue)
        {
            query = query.Where(p => p.Status == status.Value);
        }

        var payouts = await query
            .OrderBy(p => p.CreatedAt)
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, 200))
            .Join(
                _db.Users,
                payout => payout.HostId,
                user => user.Id,
                (payout, user) => new PayoutView(
                    payout.Id,
                    payout.HostId,
                    user.Phone,
                    user.FullName,
                    payout.Amount,
                    payout.Status.ToString(),
                    payout.ProviderReference,
                    payout.FailureReason,
                    payout.CreatedAt,
                    payout.CompletedAt))
            .ToListAsync(cancellationToken);

        return Ok(payouts);
    }

    /// <summary>Records that the money reached the host.</summary>
    [HttpPost("{payoutId:guid}/complete")]
    public async Task<ActionResult<PayoutView>> Complete(
        Guid payoutId,
        [FromBody] CompletePayoutRequest request,
        CancellationToken cancellationToken)
    {
        _currentUser.RequireAdmin();

        var payout = await _wallets.CompletePayoutAsync(payoutId, request.ProviderReference, cancellationToken);
        return Ok(await ViewAsync(payout.Id, cancellationToken));
    }

    /// <summary>
    /// Records that the transfer was rejected. The credits go back to the host's earning balance
    /// via a compensating Refund — they are not stranded in a payout that will never land.
    /// </summary>
    [HttpPost("{payoutId:guid}/fail")]
    public async Task<ActionResult<PayoutView>> Fail(
        Guid payoutId,
        [FromBody] FailPayoutRequest request,
        CancellationToken cancellationToken)
    {
        _currentUser.RequireAdmin();

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            // The host will read this. "Failed" with no reason is a support ticket waiting to happen.
            throw new DomainException("A reason is required so the host can be told why.");
        }

        var payout = await _wallets.FailPayoutAsync(payoutId, request.Reason.Trim(), cancellationToken);
        return Ok(await ViewAsync(payout.Id, cancellationToken));
    }

    private async Task<PayoutView> ViewAsync(Guid payoutId, CancellationToken cancellationToken) =>
        await _db.Payouts
            .Where(p => p.Id == payoutId)
            .Join(
                _db.Users,
                payout => payout.HostId,
                user => user.Id,
                (payout, user) => new PayoutView(
                    payout.Id,
                    payout.HostId,
                    user.Phone,
                    user.FullName,
                    payout.Amount,
                    payout.Status.ToString(),
                    payout.ProviderReference,
                    payout.FailureReason,
                    payout.CreatedAt,
                    payout.CompletedAt))
            .SingleAsync(cancellationToken);
}

public sealed record CompletePayoutRequest(string? ProviderReference);

public sealed record FailPayoutRequest(string Reason);

public sealed record PayoutView(
    Guid PayoutId,
    Guid HostId,
    string HostPhone,
    string HostName,
    decimal Amount,
    string Status,
    string? ProviderReference,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
