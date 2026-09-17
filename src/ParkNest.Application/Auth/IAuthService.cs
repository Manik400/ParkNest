using ParkNest.Domain.Common;

namespace ParkNest.Application.Auth;

/// <summary>
/// One-time codes to an email address or a phone number, which avoids storing passwords at all.
/// Email is the channel that costs nothing to run; SMS is the one users in this market expect and
/// is switched on by configuring a paid provider. Delivery sits behind <see cref="IOtpSender"/>.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Issues a one-time code to an email address or phone number, creating the user on first
    /// sight. Deliberately reveals nothing about whether the destination was already registered.
    /// </summary>
    Task<OtpChallenge> RequestOtpAsync(string destination, CancellationToken cancellationToken = default);

    /// <summary>Exchanges a valid code for an access token and a refresh token.</summary>
    Task<AuthResult> VerifyOtpAsync(string destination, string code, CancellationToken cancellationToken = default);

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
/// Populated only by a development sender, so the API can be driven without a mail or SMS
/// account. Null in Production — the code only ever reaches the user's inbox or handset.
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
    /// <summary>Either contact may be missing: an account created by email has no phone yet.</summary>
    (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(Guid userId, UserRole role, string? phone, string? email);
}

/// <summary>How a one-time code reaches the user.</summary>
public enum OtpChannel
{
    Sms,
    Email
}

/// <summary>
/// Delivers the one-time code over one channel. At most one sender is registered per channel; a
/// channel with none is switched off, and asking for a code on it is refused up front.
/// </summary>
public interface IOtpSender
{
    OtpChannel Channel { get; }

    /// <param name="destination">Normalised: phone digits, or a lower-cased email address.</param>
    Task SendAsync(string destination, string code, CancellationToken cancellationToken = default);

    /// <summary>Whether the code may be returned in the API response. Never true in Production.</summary>
    bool ExposesCodeInResponse { get; }
}
