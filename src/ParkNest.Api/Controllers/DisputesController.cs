using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Disputes;

namespace ParkNest.Api.Controllers;

/// <summary>
/// Raising and reading disputes, for the people involved in one.
///
/// Deciding them lives in <see cref="AdminDisputesController"/>. Same service underneath, split at
/// the route so the admin actions are not one forgotten role check away from being public.
/// </summary>
[ApiController]
[Route("api/disputes")]
public sealed class DisputesController : ControllerBase
{
    private readonly IDisputeService _disputes;

    public DisputesController(IDisputeService disputes) => _disputes = disputes;

    /// <summary>Disputes the caller is party to.</summary>
    [HttpGet("me")]
    public async Task<ActionResult<IReadOnlyList<DisputeView>>> Mine(
        [FromQuery] bool onlyOpen = false,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default) =>
        Ok(await _disputes.ListAsync(new DisputeQuery(onlyOpen, limit, offset), cancellationToken));

    /// <summary>One dispute, for a party to it or an admin.</summary>
    [HttpGet("{disputeId:guid}")]
    public async Task<ActionResult<DisputeView>> Get(Guid disputeId, CancellationToken cancellationToken) =>
        Ok(await _disputes.GetAsync(disputeId, cancellationToken));

    /// <summary>Raises a dispute against a booking the caller was part of.</summary>
    [HttpPost]
    public async Task<ActionResult<DisputeView>> Raise(
        [FromBody] RaiseDisputeRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _disputes.RaiseAsync(request, cancellationToken));
}
