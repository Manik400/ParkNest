using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Vehicles;
using ParkNest.Domain.Common;

namespace ParkNest.Api.Controllers;

[ApiController]
[Route("api/vehicles")]
public sealed class VehiclesController : ControllerBase
{
    private readonly IVehicleService _vehicles;

    public VehiclesController(IVehicleService vehicles) => _vehicles = vehicles;

    /// <summary>The caller's registered vehicles. A booking must name one of these.</summary>
    [HttpGet("me")]
    public async Task<ActionResult<IReadOnlyList<VehicleResponse>>> Mine(CancellationToken cancellationToken)
    {
        var vehicles = await _vehicles.GetMineAsync(cancellationToken);
        return Ok(vehicles.Select(VehicleResponse.From).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<VehicleResponse>> Add(
        [FromBody] AddVehicleRequest request,
        CancellationToken cancellationToken)
    {
        var vehicle = await _vehicles.AddAsync(request.PlateNumber, request.Type, cancellationToken);
        return Ok(VehicleResponse.From(vehicle));
    }

    [HttpDelete("{vehicleId:guid}")]
    public async Task<IActionResult> Remove(Guid vehicleId, CancellationToken cancellationToken)
    {
        await _vehicles.RemoveAsync(vehicleId, cancellationToken);
        return NoContent();
    }
}

public sealed record AddVehicleRequest(string PlateNumber, VehicleType Type);

public sealed record VehicleResponse(Guid Id, string PlateNumber, string Type)
{
    public static VehicleResponse From(ParkNest.Domain.Users.Vehicle vehicle) =>
        new(vehicle.Id, vehicle.PlateNumber, vehicle.Type.ToString());
}
