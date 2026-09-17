using ParkNest.Domain.Common;
using ParkNest.Domain.Users;

namespace ParkNest.Application.Vehicles;

/// <summary>
/// A renter's registered cars. Booking requires one, so until this existed the renter journey
/// could not be completed through the API at all.
/// </summary>
public interface IVehicleService
{
    Task<Vehicle> AddAsync(string plateNumber, VehicleType type, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Vehicle>> GetMineAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes one of the caller's vehicles. Refused while a booking is still open on it.</summary>
    Task RemoveAsync(Guid vehicleId, CancellationToken cancellationToken = default);
}
