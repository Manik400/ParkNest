using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Queries;
using ParkNest.Application.Wallets;

namespace ParkNest.Api.Controllers;

[ApiController]
[Route("api/wallets")]
public sealed class WalletsController : ControllerBase
{
    private readonly IWalletService _wallets;
    private readonly ILedgerService _ledger;
    private readonly ICurrentUser _currentUser;
    private readonly IParkNestQueries _queries;

    public WalletsController(
        IWalletService wallets,
        ILedgerService ledger,
        ICurrentUser currentUser,
        IParkNestQueries queries)
    {
        _wallets = wallets;
        _ledger = ledger;
        _currentUser = currentUser;
        _queries = queries;
    }

    /// <summary>The caller's credit history — the receipt trail behind their balance.</summary>
    [HttpGet("me/transactions")]
    public async Task<ActionResult<IReadOnlyList<LedgerEntrySummary>>> MyTransactions(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default) =>
        Ok(await _queries.GetMyLedgerAsync(limit, offset, cancellationToken));

    /// <summary>The caller's own wallet.</summary>
    [HttpGet("me")]
    public async Task<ActionResult<WalletResponse>> GetMine(CancellationToken cancellationToken)
    {
        var wallet = await _wallets.GetOrCreateWalletAsync(_currentUser.RequireUserId(), cancellationToken);
        return Ok(WalletResponse.From(wallet));
    }

    /// <summary>Any user's wallet. Admin only — used by dispute and support tooling.</summary>
    [HttpGet("{userId:guid}")]
    public async Task<ActionResult<WalletResponse>> Get(Guid userId, CancellationToken cancellationToken)
    {
        _currentUser.RequireSelfOrAdmin(userId);
        var wallet = await _wallets.GetOrCreateWalletAsync(userId, cancellationToken);
        return Ok(WalletResponse.From(wallet));
    }

    /// <summary>
    /// Manual credit adjustment. Admin only, and no longer the way users buy credits — that runs
    /// through <c>POST /api/payments/orders</c> and only completes on a signature-verified webhook
    /// (ADR 0004). What remains here is the support tool: refunding a botched session, seeding a
    /// test account, settling a dispute in the user's favour.
    /// </summary>
    [HttpPost("{userId:guid}/recharge")]
    public async Task<ActionResult<WalletResponse>> Recharge(
        Guid userId,
        [FromBody] RechargeRequest request,
        CancellationToken cancellationToken)
    {
        _currentUser.RequireAdmin();

        var wallet = await _wallets.RechargeAsync(userId, request.Amount, request.IdempotencyKey, cancellationToken);
        return Ok(WalletResponse.From(wallet));
    }

    /// <summary>Requests a payout of the caller's own earnings.</summary>
    [HttpPost("me/cash-out")]
    public async Task<ActionResult<CashOutResponse>> CashOut(
        [FromBody] CashOutRequest request,
        CancellationToken cancellationToken)
    {
        var payout = await _wallets.RequestCashOutAsync(
            _currentUser.RequireUserId(), request.Amount, request.IdempotencyKey, cancellationToken);

        return Ok(new CashOutResponse(payout.Id, payout.Amount, payout.Status.ToString()));
    }

    /// <summary>
    /// Replays the caller's wallet from its ledger entries and reports whether the stored balances
    /// still agree. A false <c>Matches</c> means the ledger and the cached balances have diverged
    /// and should be treated as an incident.
    /// </summary>
    [HttpGet("{userId:guid}/reconciliation")]
    public async Task<ActionResult<ReconciliationResponse>> Reconcile(Guid userId, CancellationToken cancellationToken)
    {
        _currentUser.RequireSelfOrAdmin(userId);

        var wallet = await _wallets.GetOrCreateWalletAsync(userId, cancellationToken);
        var replayed = await _ledger.RecomputeFromEntriesAsync(wallet.Id, cancellationToken);

        var matches = replayed.Spendable == wallet.SpendableBalance
                      && replayed.Held == wallet.HeldBalance
                      && replayed.Earning == wallet.EarningBalance;

        return Ok(new ReconciliationResponse(
            WalletResponse.From(wallet),
            replayed.Spendable,
            replayed.Held,
            replayed.Earning,
            matches));
    }
}

public sealed record RechargeRequest(decimal Amount, string IdempotencyKey);

public sealed record CashOutRequest(decimal Amount, string IdempotencyKey);

public sealed record CashOutResponse(Guid PayoutId, decimal Amount, string Status);

public sealed record WalletResponse(Guid Id, Guid UserId, decimal Spendable, decimal Held, decimal Earning)
{
    public static WalletResponse From(ParkNest.Domain.Wallets.Wallet wallet) =>
        new(wallet.Id, wallet.UserId, wallet.SpendableBalance, wallet.HeldBalance, wallet.EarningBalance);
}

public sealed record ReconciliationResponse(
    WalletResponse Stored,
    decimal ReplayedSpendable,
    decimal ReplayedHeld,
    decimal ReplayedEarning,
    bool Matches);
