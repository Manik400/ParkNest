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

    /// <summary>Exchanges a valid code for an access token and a refresh token.</summary>
    Task<AuthResult> VerifyOtpAsync(string phone, string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Trades a refresh token for a fresh pair. The presented token is spent in the process —
    /// rotation on every use, so a stolen one is only good until the real client next refreshes.
    /// </summary>
    Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs out. Revokes the presented refresh token and everything descended from the same
    /// sign-in, so "log out on my lost phone" means something.
    /// </summary>
    Task RevokeAsync(string refreshToken, CancellationToken cancellationToken = default);
}

/// <param name="DevCode">
/// Populated only outside Production, so the API can be driven without an SMS provider.
/// Null in Production — the code only ever reaches the user's handset.
/// </param>
public sealed record OtpChallenge(DateTimeOffset ExpiresAt, string? DevCode);

/// <param name="ExpiresAt">When the access token dies. The refresh token outlives it by a long way.</param>
public sealed record AuthResult(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    Guid UserId,
    UserRole Role,
    bool IsNewUser,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt);

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
