using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Listings;
using ParkNest.Application.Queries;
using ParkNest.Domain.Common;

namespace ParkNest.Api.Controllers;

[ApiController]
[Route("api/listings")]
public sealed class ListingsController : ControllerBase
{
    private readonly IListingService _listings;
    private readonly ISpaceSearchService _search;
    private readonly IParkNestQueries _queries;

    public ListingsController(IListingService listings, ISpaceSearchService search, IParkNestQueries queries)
    {
        _listings = listings;
        _search = search;
        _queries = queries;
    }

    /// <summary>Every listing the caller hosts, drafts included.</summary>
    [HttpGet("me")]
    public async Task<ActionResult<IReadOnlyList<ListingSummary>>> Mine(CancellationToken cancellationToken) =>
        Ok(await _queries.GetMyListingsAsync(cancellationToken));

    /// <summary>Listing detail. Published listings are public; drafts are visible only to their host.</summary>
    [AllowAnonymous]
    [HttpGet("{spaceId:guid}")]
    public async Task<ActionResult<ListingDetail>> Detail(Guid spaceId, CancellationToken cancellationToken) =>
        Ok(await _queries.GetListingAsync(spaceId, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<ListingResponse>> CreateDraft(
        [FromBody] CreateListingRequest request,
        CancellationToken cancellationToken)
    {
        var space = await _listings.CreateDraftAsync(request, cancellationToken);
        return CreatedAtAction(nameof(CreateDraft), new { id = space.Id }, ListingResponse.From(space));
    }

    /// <summary>Goes live, but only if the asking price clears the city band (PRD §9).</summary>
    [HttpPost("{spaceId:guid}/publish")]
    public async Task<ActionResult<ListingResponse>> Publish(Guid spaceId, CancellationToken cancellationToken)
    {
        var space = await _listings.PublishAsync(spaceId, cancellationToken);
        return Ok(ListingResponse.From(space));
    }

    [HttpPost("{spaceId:guid}/status")]
    public async Task<ActionResult<ListingResponse>> SetStatus(
        Guid spaceId,
        [FromBody] SetStatusRequest request,
        CancellationToken cancellationToken)
    {
        var space = await _listings.SetStatusAsync(spaceId, request.Status, cancellationToken);
        return Ok(ListingResponse.From(space));
    }

    /// <summary>Public search — a renter needs to see what's available before signing up.</summary>
    [AllowAnonymous]
    [HttpGet("nearby")]
    public async Task<ActionResult<IReadOnlyList<NearbySpace>>> Nearby(
        [FromQuery] double lat,
        [FromQuery] double lng,
        [FromQuery] int radiusMetres = 2000,
        [FromQuery] VehicleType? vehicleType = null,
        [FromQuery] decimal? maxPricePerHour = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var results = await _search.SearchNearbyAsync(
            new NearbySearchQuery(lat, lng, radiusMetres, vehicleType, maxPricePerHour, Math.Clamp(limit, 1, 200)),
            cancellationToken);

        return Ok(results);
    }
}

public sealed record SetStatusRequest(SpaceStatus Status);

public sealed record ListingResponse(
    Guid Id,
    Guid HostId,
    string Title,
    string City,
    decimal PricePerHour,
    string Status)
{
    public static ListingResponse From(ParkNest.Domain.Listings.ParkingSpace space) =>
        new(space.Id, space.HostId, space.Title, space.City, space.PricePerHour, space.Status.ToString());
}
