using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Analytics;

namespace ParkNest.Api.Controllers;

/// <summary>
/// The operator's view of the counter: who opened the site, who searched, who booked, who paid.
///
/// The authorisation is inside <see cref="IAnalyticsQueries"/> rather than on this class, because
/// it is not the plain admin check the rest of the console uses — an allow-list can narrow these
/// figures to one named person even among admins. Putting it in the service keeps the rule in one
/// place no matter what else ever reads it.
/// </summary>
[ApiController]
[Route("api/admin/analytics")]
public sealed class AdminAnalyticsController : ControllerBase
{
    private readonly IAnalyticsQueries _analytics;

    public AdminAnalyticsController(IAnalyticsQueries analytics) => _analytics = analytics;

    /// <param name="days">Window, counted in whole days back from today. Clamped to the configured ceiling.</param>
    [HttpGet("summary")]
    public async Task<ActionResult<AnalyticsSummary>> Summary(
        [FromQuery] int days = 30,
        CancellationToken cancellationToken = default) =>
        Ok(await _analytics.GetSummaryAsync(days, cancellationToken));
}
