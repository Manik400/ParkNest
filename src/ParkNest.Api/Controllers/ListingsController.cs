using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Common;
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
    private readonly IListingPhotoService _photos;

    public ListingsController(
        IListingService listings,
        ISpaceSearchService search,
        IParkNestQueries queries,
        IListingPhotoService photos)
    {
        _listings = listings;
        _search = search;
        _queries = queries;
        _photos = photos;
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

    /// <summary>Photos on a listing. Public for a published space, host-only for a draft.</summary>
    [AllowAnonymous]
    [HttpGet("{spaceId:guid}/photos")]
    public async Task<ActionResult<IReadOnlyList<ListingPhotoView>>> Photos(
        Guid spaceId,
        CancellationToken cancellationToken) =>
        Ok(await _photos.ListAsync(spaceId, cancellationToken));

    /// <summary>
    /// Uploads one photo. Multipart rather than base64 in JSON, so a five-megabyte image is not
    /// inflated by a third and held in memory as a string on the way through.
    /// </summary>
    [HttpPost("{spaceId:guid}/photos")]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<ActionResult<ListingPhotoView>> AddPhoto(
        Guid spaceId,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            throw new DomainException("Attach a photo to upload.");
        }

        await using var content = file.OpenReadStream();

        // The declared content type is not passed on deliberately: the service decides what this
        // is by reading the bytes, because the header is whatever the caller chose to send.
        return Ok(await _photos.AddAsync(spaceId, new PhotoUpload(content, file.Length), cancellationToken));
    }

    [HttpDelete("{spaceId:guid}/photos/{photoId:guid}")]
    public async Task<IActionResult> RemovePhoto(
        Guid spaceId,
        Guid photoId,
        CancellationToken cancellationToken)
    {
        await _photos.RemoveAsync(spaceId, photoId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// The code to print on the sticker at the space, minted on first ask. Host only: a code any
    /// renter could fetch would prove nothing about them having stood anywhere.
    /// </summary>
    [HttpGet("{spaceId:guid}/check-in-code")]
    public async Task<ActionResult<CheckInCode>> CheckInCode(Guid spaceId, CancellationToken cancellationToken) =>
        Ok(await _listings.GetOrCreateCheckInCodeAsync(spaceId, cancellationToken));

    /// <summary>Issues a new code and retires the old one, for a sticker that has been photographed.</summary>
    [HttpPost("{spaceId:guid}/check-in-code/rotate")]
    public async Task<ActionResult<CheckInCode>> RotateCheckInCode(Guid spaceId, CancellationToken cancellationToken) =>
        Ok(await _listings.RotateCheckInCodeAsync(spaceId, cancellationToken));

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
