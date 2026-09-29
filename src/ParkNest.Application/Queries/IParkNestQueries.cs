using ParkNest.Domain.Common;

namespace ParkNest.Application.Queries;

/// <summary>
/// Read-side projections for the clients. Separate from the command services because these do no
/// business reasoning — they authorise, project, and return. Keeping them apart stops read
/// convenience DTOs leaking into the domain services that move money.
/// </summary>
public interface IParkNestQueries
{
    /// <summary>Bookings where the caller is the renter, newest first.</summary>
    Task<IReadOnlyList<BookingSummary>> GetMyBookingsAsync(BookingFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Bookings taken against spaces the caller hosts.</summary>
    Task<IReadOnlyList<BookingSummary>> GetHostingBookingsAsync(BookingFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Full detail for one booking. Visible to its renter, its host, and admins.</summary>
    Task<BookingDetail> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>Every listing the caller hosts, including drafts.</summary>
    Task<IReadOnlyList<ListingSummary>> GetMyListingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Public detail for a listing. Unpublished listings are visible only to their host.</summary>
    Task<ListingDetail> GetListingAsync(Guid spaceId, CancellationToken cancellationToken = default);

    /// <summary>The caller's credit history, newest first — the receipt trail behind their balance.</summary>
    Task<IReadOnlyList<LedgerEntrySummary>> GetMyLedgerAsync(int limit, int offset, CancellationToken cancellationToken = default);
}

public sealed record BookingFilter(BookingStatus? Status = null, int Limit = 50, int Offset = 0);

public sealed record BookingSummary(
    Guid Id,
    Guid ParkingSpaceId,
    string SpaceTitle,
    string SpaceAddress,
    DateTimeOffset StartTime,
    DateTimeOffset ExpectedEndTime,
    DateTimeOffset? ActualEndTime,
    decimal RatePerHour,
    decimal HoldAmount,
    decimal SettledAmount,
    string Status,
    // The previous car had not left when this slot came due. Cancelling it is free.
    bool SlotBlocked);

public sealed record BookingDetail(
    BookingSummary Summary,
    Guid RenterId,
    Guid HostId,
    Guid VehicleId,
    string VehiclePlate,
    DateTimeOffset? ActualStartTime,
    int BookedMinutes,
    int? BilledMinutes,
    decimal OverstayAmount,
    decimal PlatformFee,
    decimal ShortfallAmount,
    string? StartDetectionMethod,
    string? EndDetectionMethod,
    // The over-running session that was still in the space, if there was one.
    Guid? BlockedByBookingId,
    DateTimeOffset? BlockedAt,
    IReadOnlyList<LedgerEntrySummary> LedgerEntries);

public sealed record ListingSummary(
    Guid Id,
    string Title,
    string AddressLine,
    string City,
    decimal PricePerHour,
    string Status,
    int ActiveBookings);

public sealed record ListingDetail(
    ListingSummary Summary,
    double Latitude,
    double Longitude,
    string? Zone,
    string TimeZoneId,
    IReadOnlyList<string> SupportedVehicleTypes,
    IReadOnlyList<AvailabilityWindowView> AvailabilityWindows,
    IReadOnlyList<string> PhotoUrls);

public sealed record AvailabilityWindowView(DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime);

/// <param name="Reference">The id a person quotes, <c>TXN-…</c>.</param>
/// <param name="RevertsReference">
/// Set when this transaction undoes an earlier one — the hold a release returns, the settlement
/// a dispute adjusts — so a wallet line can say which.
/// </param>
public sealed record LedgerEntrySummary(
    Guid TransactionId,
    string Reference,
    string? RevertsReference,
    string TransactionType,
    string Account,
    string Direction,
    decimal Amount,
    Guid? BookingId,
    string? Description,
    DateTimeOffset CreatedAt);
