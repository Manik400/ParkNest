using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Auth;

namespace ParkNest.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly ICurrentUser _currentUser;

    public AuthController(IAuthService auth, ICurrentUser currentUser)
    {
        _auth = auth;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Sends a one-time code to an email address or a phone number — send one or the other.
    /// Creates the account on first use.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.OtpRequest)]
    [HttpPost("request-otp")]
    public async Task<ActionResult<OtpChallenge>> RequestOtp(
        [FromBody] RequestOtpRequest request,
        CancellationToken cancellationToken)
    {
        var challenge = await _auth.RequestOtpAsync(DestinationOf(request.Email, request.Phone), cancellationToken);
        return Ok(challenge);
    }

    /// <summary>Exchanges a valid code for a bearer token.</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.OtpVerify)]
    [HttpPost("verify-otp")]
    public async Task<ActionResult<AuthResult>> VerifyOtp(
        [FromBody] VerifyOtpRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _auth.VerifyOtpAsync(DestinationOf(request.Email, request.Phone), request.Code, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Trades a refresh token for a fresh pair. The presented token is spent, so a client must
    /// store what comes back — replaying the old one is read as a leak and ends the session.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.OtpVerify)]
    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResult>> Refresh(
        [FromBody] RefreshRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _auth.RefreshAsync(request.RefreshToken, cancellationToken));

    /// <summary>
    /// Signs out, ending the session on the server rather than only in the client. Anonymous
    /// because the access token may already have expired — the refresh token is the credential
    /// being surrendered, and it authenticates the request by itself.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.OtpVerify)]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(
        [FromBody] RefreshRequest request,
        CancellationToken cancellationToken)
    {
        await _auth.RevokeAsync(request.RefreshToken, cancellationToken);
        return NoContent();
    }

    /// <summary>Who the current token belongs to. Useful for clients restoring a session.</summary>
    [HttpGet("me")]
    public ActionResult<CurrentUserResponse> Me() =>
        Ok(new CurrentUserResponse(_currentUser.RequireUserId(), _currentUser.Role?.ToString()));

    /// <summary>Email wins when both are sent; the service tells the two apart and validates.</summary>
    private static string DestinationOf(string? email, string? phone) =>
        string.IsNullOrWhiteSpace(email) ? phone ?? string.Empty : email;
}

public sealed record RequestOtpRequest(string? Phone = null, string? Email = null);

public sealed record VerifyOtpRequest(string Code, string? Phone = null, string? Email = null);

public sealed record RefreshRequest(string RefreshToken);

public sealed record CurrentUserResponse(Guid UserId, string? Role);
