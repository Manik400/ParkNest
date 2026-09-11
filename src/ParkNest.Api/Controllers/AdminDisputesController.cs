using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Disputes;

namespace ParkNest.Api.Controllers;

/// <summary>
/// The dispute queue and the decisions made on it. Every action here is admin-only, enforced in
/// the service rather than by an attribute on the route, so the check travels with the operation.
/// </summary>
[ApiController]
[Route("api/admin/disputes")]
public sealed class AdminDisputesController : ControllerBase
{
    private readonly IDisputeService _disputes;

    public AdminDisputesController(IDisputeService disputes) => _disputes = disputes;

    /// <summary>The queue. Defaults to what still needs a decision.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DisputeView>>> List(
        [FromQuery] bool onlyOpen = true,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default) =>
        Ok(await _disputes.ListAsync(new DisputeQuery(onlyOpen, limit, offset), cancellationToken));

    /// <summary>Acknowledges the dispute, so both parties can see somebody has picked it up.</summary>
    [HttpPost("{disputeId:guid}/review")]
    public async Task<ActionResult<DisputeView>> Review(Guid disputeId, CancellationToken cancellationToken) =>
        Ok(await _disputes.StartReviewAsync(disputeId, cancellationToken));

    /// <summary>
    /// Upholds the dispute. Any refund is posted as a fresh compensating transaction — the
    /// booking's original settlement is never edited.
    /// </summary>
    [HttpPost("{disputeId:guid}/resolve")]
    public async Task<ActionResult<DisputeView>> Resolve(
        Guid disputeId,
        [FromBody] ResolveDisputeBody body,
        CancellationToken cancellationToken) =>
        Ok(await _disputes.ResolveAsync(
            new ResolveDisputeRequest(disputeId, body.Resolution, body.RefundToRenter, body.ChargedToPlatform),
            cancellationToken));

    /// <summary>Rejects the dispute. No money moves; the reason goes on the record.</summary>
    [HttpPost("{disputeId:guid}/reject")]
    public async Task<ActionResult<DisputeView>> Reject(
        Guid disputeId,
        [FromBody] RejectDisputeBody body,
        CancellationToken cancellationToken) =>
        Ok(await _disputes.RejectAsync(disputeId, body.Resolution, cancellationToken));
}

/// <summary>
/// The dispute id comes from the route, so it is absent here — a body that could disagree with the
/// route about which dispute is being decided is a bug waiting for a careless client.
/// </summary>
public sealed record ResolveDisputeBody(string Resolution, decimal RefundToRenter, bool ChargedToPlatform);

public sealed record RejectDisputeBody(string Resolution);
