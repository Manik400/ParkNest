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

    /// <summary>Sends a one-time code. Creates the account on first use.</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.OtpRequest)]
    [HttpPost("request-otp")]
    public async Task<ActionResult<OtpChallenge>> RequestOtp(
        [FromBody] RequestOtpRequest request,
        CancellationToken cancellationToken)
    {
        var challenge = await _auth.RequestOtpAsync(request.Phone, cancellationToken);
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
        var result = await _auth.VerifyOtpAsync(request.Phone, request.Code, cancellationToken);
        return Ok(result);
    }

    /// <summary>Who the current token belongs to. Useful for clients restoring a session.</summary>
    [HttpGet("me")]
    public ActionResult<CurrentUserResponse> Me() =>
        Ok(new CurrentUserResponse(_currentUser.RequireUserId(), _currentUser.Role?.ToString()));
}

public sealed record RequestOtpRequest(string Phone);

public sealed record VerifyOtpRequest(string Phone, string Code);

public sealed record CurrentUserResponse(Guid UserId, string? Role);
