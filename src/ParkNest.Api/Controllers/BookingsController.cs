using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Bookings;
using ParkNest.Domain.Common;

namespace ParkNest.Api.Controllers;

[ApiController]
[Route("api/bookings")]
public sealed class BookingsController : ControllerBase
{
    private readonly IBookingService _bookings;

    public BookingsController(IBookingService bookings) => _bookings = bookings;

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
        var booking = await _bookings.StartSessionAsync(bookingId, request.Method, request.At, cancellationToken);
        return Ok(BookingResponse.From(booking));
    }

    /// <summary>Tier 1 check-out — "I'm leaving". Meters, settles, and releases in one step.</summary>
    [HttpPost("{bookingId:guid}/end")]
    public async Task<ActionResult<SessionOutcomeResponse>> End(
        Guid bookingId,
        [FromBody] SessionEventRequest request,
        CancellationToken cancellationToken)
    {
        var outcome = await _bookings.EndSessionAsync(bookingId, request.Method, request.At, cancellationToken);

        return Ok(new SessionOutcomeResponse(
            BookingResponse.From(outcome.Booking),
            outcome.BilledMinutes,
            outcome.TotalCharged,
            outcome.ReleasedToRenter,
            outcome.HostCredited,
            outcome.PlatformFee,
            outcome.Shortfall));
    }

    [HttpPost("{bookingId:guid}/cancel")]
    public async Task<ActionResult<BookingResponse>> Cancel(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await _bookings.CancelBookingAsync(bookingId, cancellationToken);
        return Ok(BookingResponse.From(booking));
    }
}

public sealed record SessionEventRequest(DetectionMethod Method = DetectionMethod.AppConfirmed, DateTimeOffset? At = null);

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
