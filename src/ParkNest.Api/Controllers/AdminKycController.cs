using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Users;
using ParkNest.Domain.Common;

namespace ParkNest.Api.Controllers;

/// <summary>
/// The identity review queue, as operations sees it.
///
/// A human decision on purpose. An automated document check needs a provider we do not have, and
/// the cost of guessing runs in both directions: approve wrongly and the platform pays real money
/// to a fabricated identity, refuse wrongly and a real host's earnings are stranded with no
/// explanation.
/// </summary>
[ApiController]
[Route("api/admin/kyc")]
public sealed class AdminKycController : ControllerBase
{
    private readonly IKycService _kyc;

    public AdminKycController(IKycService kyc) => _kyc = kyc;

    /// <summary>Pending submissions, oldest first. Pass a status to read history instead.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<KycSubmissionView>>> Queue(
        [FromQuery] KycStatus? status,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default) =>
        Ok(await _kyc.GetQueueAsync(status, limit, offset, cancellationToken));

    /// <summary>Approves the submission, which is what lets that host cash out.</summary>
    [HttpPost("{submissionId:guid}/verify")]
    public async Task<ActionResult<KycSubmissionView>> Verify(
        Guid submissionId,
        CancellationToken cancellationToken) =>
        Ok(await _kyc.VerifyAsync(submissionId, cancellationToken));

    /// <summary>Refuses it, with a reason the host is shown.</summary>
    [HttpPost("{submissionId:guid}/reject")]
    public async Task<ActionResult<KycSubmissionView>> Reject(
        Guid submissionId,
        [FromBody] RejectKycRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _kyc.RejectAsync(submissionId, request.Reason, cancellationToken));
}

public sealed record RejectKycRequest(string Reason);
