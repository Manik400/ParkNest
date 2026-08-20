using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Bookings;
using ParkNest.Application.Queries;
using ParkNest.Domain.Common;

namespace ParkNest.Api.Controllers;

[ApiController]
[Route("api/bookings")]
public sealed class BookingsController : ControllerBase
{
    private readonly IBookingService _bookings;
    private readonly IParkNestQueries _queries;

    public BookingsController(IBookingService bookings, IParkNestQueries queries)
    {
        _bookings = bookings;
        _queries = queries;
    }

    /// <summary>The caller's own bookings, newest first.</summary>
    [HttpGet("me")]
    public async Task<ActionResult<IReadOnlyList<BookingSummary>>> Mine(
        [FromQuery] BookingStatus? status,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default) =>
        Ok(await _queries.GetMyBookingsAsync(new BookingFilter(status, limit, offset), cancellationToken));

    /// <summary>Bookings taken against spaces the caller hosts.</summary>
    [HttpGet("hosting")]
    public async Task<ActionResult<IReadOnlyList<BookingSummary>>> Hosting(
        [FromQuery] BookingStatus? status,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default) =>
        Ok(await _queries.GetHostingBookingsAsync(new BookingFilter(status, limit, offset), cancellationToken));

    /// <summary>Full detail including the ledger entries behind the settlement.</summary>
    [HttpGet("{bookingId:guid}")]
    public async Task<ActionResult<BookingDetail>> Detail(Guid bookingId, CancellationToken cancellationToken) =>
        Ok(await _queries.GetBookingAsync(bookingId, cancellationToken));

    /// <summary>
    /// Prices a prospective booking without reserving anything, and says why it would be refused.
    /// Anonymous so a visitor can see the cost before signing up.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("quote")]
    public async Task<ActionResult<BookingQuote>> Quote(
        [FromQuery] Guid spaceId,
        [FromQuery] DateTimeOffset startTime,
        [FromQuery] int durationMinutes,
        CancellationToken cancellationToken) =>
        Ok(await _bookings.QuoteAsync(spaceId, startTime, durationMinutes, cancellationToken));

    /// <summary>Reserves credits and creates the booking. 402 if the renter is short.</summary>
    [HttpPost]
    public async Task<ActionResult<BookingResponse>> Create(
        [FromBody] CreateBookingRequest request,
        CancellationToken cancellationToken)
    {
        var booking = await _bookings.CreateBookingAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Create), new { id = booking.Id }, BookingResponse.From(booking));
    }

    /// <summary>Tier 1 check-in — "I've parked".</summary>
    [HttpPost("{bookingId:guid}/start")]
    public async Task<ActionResult<BookingResponse>> Start(
        Guid bookingId,
        [FromBody] SessionEventRequest request,
        CancellationToken cancellationToken)
    {
        var booking = await _bookings.StartSessionAsync(
            bookingId, request.Method, request.At, request.Proof(), cancellationToken);
        return Ok(BookingResponse.From(booking));
    }

    /// <summary>Tier 1 check-out — "I'm leaving". Meters, settles, and releases in one step.</summary>
    [HttpPost("{bookingId:guid}/end")]
    public async Task<ActionResult<SessionOutcomeResponse>> End(
        Guid bookingId,
        [FromBody] SessionEventRequest request,
        CancellationToken cancellationToken)
    {
        var outcome = await _bookings.EndSessionAsync(
            bookingId, request.Method, request.At, request.Proof(), cancellationToken);

        return Ok(new SessionOutcomeResponse(
            BookingResponse.From(outcome.Booking),
            outcome.BilledMinutes,
            outcome.TotalCharged,
            outcome.ReleasedToRenter,
            outcome.HostCredited,
            outcome.PlatformFee,
            outcome.Shortfall));
    }

    /// <summary>
    /// What cancelling now would cost. The client asks before showing a confirmation, so the
    /// figure comes from the same rule that will be applied rather than a copy of the policy.
    /// </summary>
    [HttpGet("{bookingId:guid}/cancellation")]
    public async Task<ActionResult<CancellationTerms>> CancellationTerms(
        Guid bookingId,
        CancellationToken cancellationToken) =>
        Ok(await _bookings.PreviewCancellationAsync(bookingId, cancellationToken));

    [HttpPost("{bookingId:guid}/cancel")]
    public async Task<ActionResult<CancellationResponse>> Cancel(Guid bookingId, CancellationToken cancellationToken)
    {
        var outcome = await _bookings.CancelBookingAsync(bookingId, cancellationToken);

        return Ok(new CancellationResponse(
            BookingResponse.From(outcome.Booking),
            outcome.Fee,
            outcome.Refund));
    }
}

public sealed record CancellationResponse(BookingResponse Booking, decimal Fee, decimal Refund);

/// <param name="QrToken">The code from the sticker at the space, for a Tier 2 scan.</param>
/// <param name="Latitude">Where the phone thinks it is, corroborating the scan.</param>
public sealed record SessionEventRequest(
    DetectionMethod Method = DetectionMethod.AppConfirmed,
    DateTimeOffset? At = null,
    string? QrToken = null,
    double? Latitude = null,
    double? Longitude = null)
{
    /// <summary>
    /// The proof, when all three parts are present. A partial scan is left as null so the service
    /// gives one clear answer about what is missing rather than the model binder rejecting the
    /// request over a field a Tier 1 caller never sends.
    /// </summary>
    public CheckInProof? Proof() =>
        QrToken is { Length: > 0 } token && Latitude is { } latitude && Longitude is { } longitude
            ? new CheckInProof(token, latitude, longitude)
            : null;
}

public sealed record BookingResponse(
    Guid Id,
    Guid ParkingSpaceId,
    Guid RenterId,
    Guid HostId,
    DateTimeOffset StartTime,
    DateTimeOffset ExpectedEndTime,
    int BookedMinutes,
    decimal HoldAmount,
    string Status)
{
    public static BookingResponse From(ParkNest.Domain.Bookings.Booking booking) =>
        new(booking.Id, booking.ParkingSpaceId, booking.RenterId, booking.HostId,
            booking.StartTime, booking.ExpectedEndTime, booking.BookedMinutes,
            booking.HoldAmount, booking.Status.ToString());
}

public sealed record SessionOutcomeResponse(
    BookingResponse Booking,
    int BilledMinutes,
    decimal TotalCharged,
    decimal ReleasedToRenter,
    decimal HostCredited,
    decimal PlatformFee,
    decimal Shortfall);
