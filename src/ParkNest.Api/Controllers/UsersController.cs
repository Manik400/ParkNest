using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Users;

namespace ParkNest.Api.Controllers;

/// <summary>The caller's own account. Sign-in contacts are shown but only changed through sign-in.</summary>
[ApiController]
[Route("api/users")]
public sealed class UsersController : ControllerBase
{
    private readonly IProfileService _profile;

    public UsersController(IProfileService profile) => _profile = profile;

    [HttpGet("me")]
    public async Task<ActionResult<ProfileView>> Me(CancellationToken cancellationToken) =>
        Ok(await _profile.GetMineAsync(cancellationToken));

    /// <summary>
    /// Updates the name and the payment phone. A field left null is untouched; an empty payment
    /// phone clears it.
    /// </summary>
    [HttpPut("me")]
    public async Task<ActionResult<ProfileView>> Update(
        [FromBody] UpdateProfileRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _profile.UpdateMineAsync(request.FullName, request.PaymentPhone, cancellationToken));
}

/// <param name="PaymentPhone">
/// The mobile number handed to the payment gateway for an account that signed in by email. Not a
/// sign-in identity, so it needs no verification code.
/// </param>
public sealed record UpdateProfileRequest(string? FullName = null, string? PaymentPhone = null);
