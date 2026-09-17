using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Disputes;
using ParkNest.Application.Common;
using ParkNest.Domain.Common;

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

    /// <summary>
    /// Attaches a photograph — the blocked bay, the damage, the empty space the renter never
    /// used. Multipart, and one file per call, because that is how a phone sends a picture.
    /// </summary>
    [HttpPost("{disputeId:guid}/evidence")]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<ActionResult<DisputeView>> AddEvidence(
        Guid disputeId,
        IFormFile file,
        [FromForm] string? note,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            throw new DomainException("Attach a photo to upload.");
        }

        await using var content = file.OpenReadStream();

        return Ok(await _disputes.AddEvidenceAsync(
            disputeId, new PhotoUpload(content, file.Length), note, cancellationToken));
    }
}
