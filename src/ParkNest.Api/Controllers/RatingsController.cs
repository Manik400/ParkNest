using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Ratings;

namespace ParkNest.Api.Controllers;

/// <summary>
/// What each party thought of the other, and the reputation that builds from it.
/// </summary>
[ApiController]
[Route("api/ratings")]
public sealed class RatingsController : ControllerBase
{
    private readonly IRatingService _ratings;

    public RatingsController(IRatingService ratings) => _ratings = ratings;

    /// <summary>Rates the other party to a finished booking.</summary>
    [HttpPost]
    public async Task<ActionResult<RatingView>> Rate(
        [FromBody] RateRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _ratings.RateAsync(request, cancellationToken));

    /// <summary>
    /// Whether this booking is still waiting on the caller's rating. Lets a client prompt without
    /// guessing at the rules, or offering a form that will be refused.
    /// </summary>
    [HttpGet("prompt/{bookingId:guid}")]
    public async Task<ActionResult<RatingPrompt>> Prompt(Guid bookingId, CancellationToken cancellationToken) =>
        Ok(await _ratings.GetPromptAsync(bookingId, cancellationToken));

    /// <summary>
    /// A user's reputation. Visible to any signed-in caller, because deciding whether to park in a
    /// stranger's driveway is the decision it exists to inform.
    /// </summary>
    [HttpGet("users/{userId:guid}")]
    public async Task<ActionResult<ReputationView>> Reputation(Guid userId, CancellationToken cancellationToken) =>
        Ok(await _ratings.GetReputationAsync(userId, cancellationToken));
}
