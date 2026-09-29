using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Listings;

namespace ParkNest.Api.Controllers;

/// <summary>
/// Where the marketplace operates, and where people wish it did.
/// </summary>
[ApiController]
[Route("api/cities")]
public sealed class CitiesController : ControllerBase
{
    private readonly ICityService _cities;

    public CitiesController(ICityService cities) => _cities = cities;

    /// <summary>The cities a space can be listed in. Public: the login page's search box needs it too.</summary>
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CityView>>> List(CancellationToken cancellationToken) =>
        Ok(await _cities.ListAsync(cancellationToken));

    /// <summary>Asks for a city that is not on the list.</summary>
    [HttpPost("requests")]
    public async Task<IActionResult> Request(
        [FromBody] CityRequestBody body,
        CancellationToken cancellationToken)
    {
        await _cities.RequestAsync(body.City, body.Note, cancellationToken);
        return NoContent();
    }

    /// <summary>The asks, most-wanted first. Admin only — enforced in the service.</summary>
    [HttpGet("requests")]
    public async Task<ActionResult<IReadOnlyList<CityRequestSummary>>> Requests(CancellationToken cancellationToken) =>
        Ok(await _cities.ListRequestsAsync(cancellationToken));
}

public sealed record CityRequestBody(string City, string? Note = null);
