using ParkNest.Domain.Bookings;
using ParkNest.Domain.Common;

namespace ParkNest.Application.Bookings;

public interface IBookingService
{
    /// <summary>
    /// Prices a prospective booking and reports whether it could actually be made, without
    /// reserving anything. Lets the UI show the cost and the reason it would be refused before
    /// the renter commits.
    /// </summary>
    Task<BookingQuote> QuoteAsync(
        Guid parkingSpaceId,
        DateTimeOffset startTime,
        int durationMinutes,
        CancellationToken cancellationToken = default);

    /// <summary>Quotes the session, reserves the credits, and creates the booking in <c>Held</c>.</summary>
    Task<Booking> CreateBookingAsync(CreateBookingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Renter has arrived. Starts the meter.</summary>
    Task<Booking> StartSessionAsync(Guid bookingId, DetectionMethod method, DateTimeOffset? at = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renter has left. Measures the real duration, bills it, releases whatever is unused, and
    /// moves the used portion to the host less commission (PRD §5.1.3).
    /// </summary>
    Task<SessionOutcome> EndSessionAsync(Guid bookingId, DetectionMethod method, DateTimeOffset? at = null, CancellationToken cancellationToken = default);

    /// <summary>Cancels a booking that never started and returns the full hold.</summary>
    Task<Booking> CancelBookingAsync(Guid bookingId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Note the absence of a renter id: the renter is always the authenticated caller. Accepting one
/// here would mean trusting the client's claim about who it is.
/// </summary>
public sealed record CreateBookingRequest(
    Guid ParkingSpaceId,
    Guid VehicleId,
    DateTimeOffset StartTime,
    int DurationMinutes,
    string IdempotencyKey);

/// <param name="Unavailable">Why it cannot be booked, or null when it can.</param>
public sealed record BookingQuote(
    Guid ParkingSpaceId,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    int BilledMinutes,
    decimal RatePerHour,
    decimal Amount,
    decimal OverstayRatePerHour,
    bool CanBook,
    string? Unavailable);

/// <param name="Shortfall">Credits owed that the renter could not cover; non-zero means a violation.</param>
public sealed record SessionOutcome(
    Booking Booking,
    int BilledMinutes,
    decimal TotalCharged,
    decimal ReleasedToRenter,
    decimal HostCredited,
    decimal PlatformFee,
    decimal Shortfall);
