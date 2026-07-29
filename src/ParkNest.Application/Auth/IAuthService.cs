using ParkNest.Domain.Common;

namespace ParkNest.Application.Auth;

/// <summary>
/// Phone + OTP, which is the login users actually expect in this market and avoids storing
/// passwords at all. The OTP delivery channel is abstracted behind <see cref="IOtpSender"/>.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Issues a one-time code for the phone number, creating the user on first sight.
    /// Deliberately reveals nothing about whether the number was already registered.
    /// </summary>
    Task<OtpChallenge> RequestOtpAsync(string phone, CancellationToken cancellationToken = default);

    /// <summary>Exchanges a valid code for an access token.</summary>
    Task<AuthResult> VerifyOtpAsync(string phone, string code, CancellationToken cancellationToken = default);
}

/// <param name="DevCode">
/// Populated only outside Production, so the API can be driven without an SMS provider.
/// Null in Production — the code only ever reaches the user's handset.
/// </param>
public sealed record OtpChallenge(DateTimeOffset ExpiresAt, string? DevCode);

public sealed record AuthResult(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    Guid UserId,
    UserRole Role,
    bool IsNewUser);

/// <summary>Issues signed access tokens.</summary>
public interface ITokenService
{
    (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(Guid userId, UserRole role, string phone);
}

/// <summary>
/// Delivers the one-time code. A logging implementation is used in development; production wires
/// this to an SMS gateway.
/// </summary>
public interface IOtpSender
{
    Task SendAsync(string phone, string code, CancellationToken cancellationToken = default);

    /// <summary>Whether the code may be returned in the API response. Never true in Production.</summary>
    bool ExposesCodeInResponse { get; }
}
