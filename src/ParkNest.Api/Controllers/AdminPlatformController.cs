using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Admin;
using ParkNest.Application.Wallets;

namespace ParkNest.Api.Controllers;

/// <summary>
/// The platform's own figures and its one destructive lever. Every action is admin-only,
/// enforced in the services so the check travels with the operation.
/// </summary>
[ApiController]
[Route("api/admin/platform")]
public sealed class AdminPlatformController : ControllerBase
{
    private readonly IPlatformRevenueQueries _revenue;
    private readonly IDataResetService _reset;

    public AdminPlatformController(IPlatformRevenueQueries revenue, IDataResetService reset)
    {
        _revenue = revenue;
        _reset = reset;
    }

    /// <summary>Commission kept, read straight off the ledger's platform account.</summary>
    [HttpGet("revenue")]
    public async Task<ActionResult<PlatformRevenue>> Revenue(CancellationToken cancellationToken) =>
        Ok(await _revenue.GetAsync(cancellationToken));

    /// <summary>Whether the reset is available here, and the phrase it needs. Admin only.</summary>
    [HttpGet("reset")]
    public ActionResult<DataResetInfo> ResetInfo() =>
        Ok(new DataResetInfo(_reset.IsAllowed, _reset.ConfirmationPhrase));

    /// <summary>
    /// Wipes every booking, wallet, ledger row, listing, payment and dispute. Accounts and price
    /// bands survive. Refused unless <c>Maintenance:AllowDataReset</c> is on and the phrase matches.
    /// </summary>
    [HttpPost("reset")]
    public async Task<ActionResult<DataResetResult>> Reset(
        [FromBody] DataResetRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _reset.ResetAsync(request.Confirmation, cancellationToken));
}

public sealed record DataResetInfo(bool Allowed, string ConfirmationPhrase);

public sealed record DataResetRequest(string Confirmation);
