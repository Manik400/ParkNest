using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Abstractions;
using ParkNest.Domain.Bookings;
using ParkNest.Domain.Common;
using ParkNest.Domain.Users;

namespace ParkNest.Application.Vehicles;

public sealed class VehicleService : IVehicleService
{
    private readonly IParkNestDbContext _db;
    private readonly ICurrentUser _currentUser;

    public VehicleService(IParkNestDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<Vehicle> AddAsync(
        string plateNumber,
        VehicleType type,
        CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();
        var plate = NormalisePlate(plateNumber);

        // Re-registering the same plate is a no-op rather than an error — a renter tapping "add"
        // twice should not see a failure for a car they already have.
        var existing = await _db.Vehicles
            .FirstOrDefaultAsync(v => v.UserId == userId && v.PlateNumber == plate, cancellationToken);

        if (existing is not null)
        {
            existing.Type = type;
            await _db.SaveChangesAsync(cancellationToken);
            return existing;
        }

        // A plate identifies one physical car. Letting two accounts claim it would break Tier 3
        // ANPR matching (PRD §13), where an observed plate has to resolve to exactly one booking.
        var claimedByAnother = await _db.Vehicles
            .AnyAsync(v => v.PlateNumber == plate && v.UserId != userId, cancellationToken);

        if (claimedByAnother)
        {
            throw new DomainException(
                $"{plate} is already registered to another account. Contact support if you have bought this vehicle.");
        }

        var vehicle = new Vehicle
        {
            UserId = userId,
            PlateNumber = plate,
            Type = type
        };

        _db.Vehicles.Add(vehicle);
        await _db.SaveChangesAsync(cancellationToken);

        return vehicle;
    }

    public async Task<IReadOnlyList<Vehicle>> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();

        return await _db.Vehicles
            .Where(v => v.UserId == userId)
            .OrderBy(v => v.PlateNumber)
            .ToListAsync(cancellationToken);
    }

    public async Task RemoveAsync(Guid vehicleId, CancellationToken cancellationToken = default)
    {
        var vehicle = await _db.Vehicles.FirstOrDefaultAsync(v => v.Id == vehicleId, cancellationToken)
                      ?? throw new DomainException("That vehicle does not exist.");

        _currentUser.RequireSelfOrAdmin(vehicle.UserId);

        // Bookings reference the vehicle for the detection trail, so removing one mid-session
        // would leave a live booking pointing at nothing.
        var inUse = await _db.Bookings.AnyAsync(
            b => b.VehicleId == vehicleId
                 && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Active),
            cancellationToken);

        if (inUse)
        {
            throw new DomainException("This vehicle has an open booking. End or cancel it first.");
        }

        _db.Vehicles.Remove(vehicle);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Upper-cased, with spaces and hyphens stripped, so "ka 01-ab 1234" and "KA01AB1234" are the
    /// same car. ANPR reads plates without separators, so this is also the form it will match on.
    /// </summary>
    private static string NormalisePlate(string plateNumber)
    {
        if (string.IsNullOrWhiteSpace(plateNumber))
        {
            throw new DomainException("A number plate is required.");
        }

        var plate = new string(plateNumber.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        if (plate.Length is < 4 or > 15)
        {
            throw new DomainException("That does not look like a valid number plate.");
        }

        return plate;
    }
}
