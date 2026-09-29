using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ParkNest.Application.Analytics;

namespace ParkNest.Api.Controllers;

/// <summary>
/// Where the site says it was opened.
///
/// Anonymous by necessity — a visit that mattered happened before anyone signed in — and therefore
/// treated as hostile input throughout: the event name is matched against a fixed list, the path
/// is stripped of its query string, and nothing about the caller is stored beyond an id their own
/// browser made up. A bearer token, if one is sent, attributes the hit to that account; a body
/// that claimed a user id would be ignored, because it has no business being believed.
/// </summary>
[ApiController]
[Route("api/analytics")]
public sealed class AnalyticsController : ControllerBase
{
    private readonly IAnalyticsCollector _collector;

    public AnalyticsController(IAnalyticsCollector collector) => _collector = collector;

    /// <summary>
    /// Records one hit. Answers 204 whether or not it was kept — a client has nothing useful to do
    /// with a refusal, and an error that distinguished a known name from an unknown one would only
    /// help somebody looking for the names that count.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.AnalyticsCollect)]
    [HttpPost("collect")]
    public async Task<IActionResult> Collect(
        [FromBody] CollectRequest request,
        CancellationToken cancellationToken)
    {
        await _collector.CollectAsync(
            new ClientAnalyticsHit(
                request.Name,
                request.VisitorId,
                request.SessionId,
                request.Path,
                request.Referrer,
                request.Source,
                request.Detail),
            cancellationToken);

        return NoContent();
    }
}

/// <param name="Name">One of the client-reportable names; anything else is dropped.</param>
/// <param name="VisitorId">The browser's own random id. Letters, digits and dashes, 64 characters at most.</param>
/// <param name="SessionId">One sitting, reset when the tab closes.</param>
/// <param name="Path">Route path. A query string is cut off here rather than stored.</param>
public sealed record CollectRequest(
    string Name,
    string? VisitorId = null,
    string? SessionId = null,
    string? Path = null,
    string? Referrer = null,
    string? Source = null,
    string? Detail = null);
