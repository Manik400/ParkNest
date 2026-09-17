using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Abstractions;
using ParkNest.Domain.Common;

namespace ParkNest.Application.Queries;

public sealed class ParkNestQueries : IParkNestQueries
{
    /// <summary>Caps an over-eager or malicious page size.</summary>
    private const int MaxPageSize = 200;

    private readonly IParkNestDbContext _db;
    private readonly ICurrentUser _currentUser;

    public ParkNestQueries(IParkNestDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<BookingSummary>> GetMyBookingsAsync(
        BookingFilter filter,
        CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();
        return await QueryBookingsAsync(b => b.RenterId == userId, filter, cancellationToken);
    }

    public async Task<IReadOnlyList<BookingSummary>> GetHostingBookingsAsync(
        BookingFilter filter,
        CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();
        return await QueryBookingsAsync(b => b.HostId == userId, filter, cancellationToken);
    }

    public async Task<BookingDetail> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var caller = _currentUser.RequireUserId();

        var booking = await _db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
                      ?? throw new DomainException($"Booking {bookingId} does not exist.");

        // Both sides of the transaction can see it — the host needs the record too — but nobody else.
        if (booking.RenterId != caller && booking.HostId != caller && _currentUser.Role != UserRole.Admin)
        {
            throw new ForbiddenException();
        }

        var space = await _db.ParkingSpaces
            .Where(s => s.Id == booking.ParkingSpaceId)
            .Select(s => new { s.Title, s.AddressLine })
            .FirstOrDefaultAsync(cancellationToken);

        var plate = await _db.Vehicles
            .Where(v => v.Id == booking.VehicleId)
            .Select(v => v.PlateNumber)
            .FirstOrDefaultAsync(cancellationToken);

        var entries = await _db.LedgerEntries
            .Where(e => e.Transaction!.BookingId == bookingId)
            .OrderBy(e => e.CreatedAt)
            .Select(e => new LedgerEntrySummary(
                e.LedgerTransactionId,
                e.Transaction!.Type.ToString(),
                e.AccountType.ToString(),
                e.Direction.ToString(),
                e.Amount,
                e.Transaction.BookingId,
                e.Transaction.Description,
                e.CreatedAt))
            .ToListAsync(cancellationToken);

        var summary = new BookingSummary(
            booking.Id,
            booking.ParkingSpaceId,
            space?.Title ?? string.Empty,
            space?.AddressLine ?? string.Empty,
            booking.StartTime,
            booking.ExpectedEndTime,
            booking.ActualEndTime,
            booking.RatePerHour,
            booking.HoldAmount,
            booking.SettledAmount,
            booking.Status.ToString(),
            booking.WasBlocked);

        return new BookingDetail(
            summary,
            booking.RenterId,
            booking.HostId,
            booking.VehicleId,
            plate ?? string.Empty,
            booking.ActualStartTime,
            booking.BookedMinutes,
            booking.BilledMinutes,
            booking.OverstayAmount,
            booking.PlatformFee,
            booking.ShortfallAmount,
            booking.StartDetectionMethod?.ToString(),
            booking.EndDetectionMethod?.ToString(),
            booking.BlockedByBookingId,
            booking.BlockedAt,
            entries);
    }

    public async Task<IReadOnlyList<ListingSummary>> GetMyListingsAsync(CancellationToken cancellationToken = default)
    {
        var hostId = _currentUser.RequireUserId();

        return await _db.ParkingSpaces
            .Where(s => s.HostId == hostId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new ListingSummary(
                s.Id,
                s.Title,
                s.AddressLine,
                s.City,
                s.PricePerHour,
                s.Status.ToString(),
                _db.Bookings.Count(b => b.ParkingSpaceId == s.Id
                                        && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Active))))
            .ToListAsync(cancellationToken);
    }

    public async Task<ListingDetail> GetListingAsync(Guid spaceId, CancellationToken cancellationToken = default)
    {
        var space = await _db.ParkingSpaces
            .Include(s => s.SupportedVehicleTypes)
            .Include(s => s.AvailabilityWindows)
            .Include(s => s.Photos)
            .FirstOrDefaultAsync(s => s.Id == spaceId, cancellationToken)
            ?? throw new DomainException($"Parking space {spaceId} does not exist.");

        // A draft or delisted space is the host's private business until they publish it.
        if (space.Status != SpaceStatus.Published)
        {
            _currentUser.RequireSelfOrAdmin(space.HostId);
        }

        var activeBookings = await _db.Bookings.CountAsync(
            b => b.ParkingSpaceId == spaceId
                 && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Active),
            cancellationToken);

        var summary = new ListingSummary(
            space.Id,
            space.Title,
            space.AddressLine,
            space.City,
            space.PricePerHour,
            space.Status.ToString(),
            activeBookings);

        return new ListingDetail(
            summary,
            space.Latitude,
            space.Longitude,
            space.Zone,
            space.TimeZoneId,
            space.SupportedVehicleTypes.Select(v => v.VehicleType.ToString()).ToList(),
            space.AvailabilityWindows
                .OrderBy(w => w.DayOfWeek).ThenBy(w => w.StartTime)
                .Select(w => new AvailabilityWindowView(w.DayOfWeek, w.StartTime, w.EndTime))
                .ToList(),
            space.Photos.OrderBy(p => p.SortOrder).Select(p => p.Url).ToList());
    }

    public async Task<IReadOnlyList<LedgerEntrySummary>> GetMyLedgerAsync(
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();

        var walletId = await _db.Wallets
            .Where(w => w.UserId == userId)
            .Select(w => (Guid?)w.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (walletId is null)
        {
            return Array.Empty<LedgerEntrySummary>();
        }

        return await _db.LedgerEntries
            .Where(e => e.WalletId == walletId)
            .OrderByDescending(e => e.CreatedAt)
            .Skip(Math.Max(offset, 0))
            .Take(Math.Clamp(limit, 1, MaxPageSize))
            .Select(e => new LedgerEntrySummary(
                e.LedgerTransactionId,
                e.Transaction!.Type.ToString(),
                e.AccountType.ToString(),
                e.Direction.ToString(),
                e.Amount,
                e.Transaction.BookingId,
                e.Transaction.Description,
                e.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<BookingSummary>> QueryBookingsAsync(
        System.Linq.Expressions.Expression<Func<Domain.Bookings.Booking, bool>> predicate,
        BookingFilter filter,
        CancellationToken cancellationToken)
    {
        var query = _db.Bookings.Where(predicate);

        if (filter.Status is { } status)
        {
            query = query.Where(b => b.Status == status);
        }

        return await query
            .OrderByDescending(b => b.StartTime)
            .Skip(Math.Max(filter.Offset, 0))
            .Take(Math.Clamp(filter.Limit, 1, MaxPageSize))
            .Select(b => new BookingSummary(
                b.Id,
                b.ParkingSpaceId,
                _db.ParkingSpaces.Where(s => s.Id == b.ParkingSpaceId).Select(s => s.Title).FirstOrDefault()!,
                _db.ParkingSpaces.Where(s => s.Id == b.ParkingSpaceId).Select(s => s.AddressLine).FirstOrDefault()!,
                b.StartTime,
                b.ExpectedEndTime,
                b.ActualEndTime,
                b.RatePerHour,
                b.HoldAmount,
                b.SettledAmount,
                b.Status.ToString(),
                b.BlockedByBookingId != null))
            .ToListAsync(cancellationToken);
    }
}
