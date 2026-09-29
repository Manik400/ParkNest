using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ParkNest.Application.Support;

namespace ParkNest.Api.Controllers;

/// <summary>
/// "Report a problem" and "Send feedback", mailed straight to the platform owner.
/// </summary>
[ApiController]
[Route("api/feedback")]
public sealed class FeedbackController : ControllerBase
{
    private readonly IFeedbackService _feedback;

    public FeedbackController(IFeedbackService feedback) => _feedback = feedback;

    /// <summary>
    /// Anonymous on purpose: someone who cannot sign in is exactly who needs to say so. A signed-in
    /// caller is still recognised, and their account email used when they leave the field empty.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Feedback)]
    [HttpPost]
    public async Task<IActionResult> Send([FromBody] FeedbackBody body, CancellationToken cancellationToken)
    {
        await _feedback.SendAsync(
            new FeedbackInput(
                body.Kind,
                body.Message,
                body.Email,
                body.Name,
                body.Page,
                body.Client,
                Request.Headers.UserAgent.ToString()),
            cancellationToken);

        return NoContent();
    }
}

public sealed record FeedbackBody(
    FeedbackKind Kind,
    string Message,
    string? Email = null,
    string? Name = null,
    string? Page = null,
    string? Client = null);
