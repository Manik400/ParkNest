using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Notifications;

namespace ParkNest.Api.Controllers;

/// <summary>
/// Where to push to reach this user.
///
/// The app registers on every launch, because the platform rotates its token whenever it likes,
/// and unregisters on sign-out — a shared handset must stop receiving the previous account's
/// bookings the moment someone else signs in on it.
/// </summary>
[ApiController]
[Route("api/devices")]
public sealed class DevicesController : ControllerBase
{
    private readonly IDeviceTokenService _devices;

    public DevicesController(IDeviceTokenService devices) => _devices = devices;

    [HttpPost]
    public async Task<IActionResult> Register(
        [FromBody] RegisterDevice request,
        CancellationToken cancellationToken)
    {
        await _devices.RegisterAsync(request, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Stops pushes to one install. The token is in the body rather than the path because an FCM
    /// registration token is long and full of characters a URL segment would have to escape.
    /// </summary>
    [HttpPost("unregister")]
    public async Task<IActionResult> Unregister(
        [FromBody] UnregisterDevice request,
        CancellationToken cancellationToken)
    {
        await _devices.UnregisterAsync(request.Token, cancellationToken);
        return NoContent();
    }
}

public sealed record UnregisterDevice(string Token);
