using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;
using ParkNest.Domain.Users;

namespace ParkNest.Application.Auth;

public sealed class AuthService : IAuthService
{
    private readonly IParkNestDbContext _db;
    private readonly ITokenService _tokens;
    private readonly IOtpSender _otpSender;
    private readonly IClock _clock;
    private readonly AuthOptions _options;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IParkNestDbContext db,
        ITokenService tokens,
        IOtpSender otpSender,
        IClock clock,
        IOptions<AuthOptions> options,
        ILogger<AuthService> logger)
    {
        _db = db;
        _tokens = tokens;
        _otpSender = otpSender;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<OtpChallenge> RequestOtpAsync(string phone, CancellationToken cancellationToken = default)
    {
        phone = NormalisePhone(phone);

        var now = _clock.UtcNow;

        await EnforceRequestLimitsAsync(phone, now, cancellationToken);

        var code = GenerateCode();

        // Any earlier live code for this number is burned, so requesting a new one cannot be used
        // to widen the guessing window on the old one.
        var outstanding = await _db.OtpCodes
            .Where(o => o.Phone == phone && o.ConsumedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var stale in outstanding)
        {
            stale.ConsumedAt = now;
        }

        var otp = new OtpCode
        {
            Phone = phone,
            CodeHash = HashCode(phone, code),
            ExpiresAt = now.AddMinutes(_options.OtpLifetimeMinutes),
            CreatedAt = now
        };

        _db.OtpCodes.Add(otp);
        await _db.SaveChangesAsync(cancellationToken);

        await _otpSender.SendAsync(phone, code, cancellationToken);

        return new OtpChallenge(otp.ExpiresAt, _otpSender.ExposesCodeInResponse ? code : null);
    }

    public async Task<AuthResult> VerifyOtpAsync(string phone, string code, CancellationToken cancellationToken = default)
    {
        phone = NormalisePhone(phone);
        var now = _clock.UtcNow;

        var otp = await _db.OtpCodes
            .Where(o => o.Phone == phone && o.ConsumedAt == null)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        // One message for every failure mode: wrong code, expired code, no code requested. Telling
        // the caller which one it was hands an attacker a probe for valid phone numbers.
        if (otp is null || !otp.IsUsable(now, _options.OtpMaxAttempts))
        {
            throw new UnauthorizedException("That code is not valid. Request a new one.");
        }

        var expected = HashCode(phone, code);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(otp.CodeHash),
                Encoding.UTF8.GetBytes(expected)))
        {
            otp.AttemptCount++;
            await _db.SaveChangesAsync(cancellationToken);
            throw new UnauthorizedException("That code is not valid. Request a new one.");
        }

        otp.ConsumedAt = now;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Phone == phone, cancellationToken);
        var isNewUser = user is null;

        if (user is null)
        {
            user = new User
            {
                Phone = phone,
                FullName = string.Empty,
                // Both, because a user is free to list a space and rent one; the API authorises
                // per action, not by locking the account into a single side of the marketplace.
                Role = _options.AdminPhones.Contains(phone) ? UserRole.Admin : UserRole.Both,
                CreatedAt = now
            };
            _db.Users.Add(user);
        }

        await _db.SaveChangesAsync(cancellationToken);

        var (result, _) = await IssueAsync(user, Guid.NewGuid(), isNewUser, cancellationToken);

        return result;
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var hash = HashRefreshToken(refreshToken);

        var stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (stored is null)
        {
            throw new UnauthorizedException("That session is no longer valid. Sign in again.");
        }

        if (stored.RevokedAt is not null)
        {
            // A token that was already spent is being presented again. Either it leaked, or a
            // client is retrying a refresh whose response it never received — and we cannot tell
            // which from here. Killing the whole family is the safe reading: the honest client
            // signs in again, the thief gets nothing.
            await RevokeFamilyAsync(stored.FamilyId, now, "Replayed after rotation.", cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Refresh token replay detected for user {UserId}; revoked session family {FamilyId}.",
                stored.UserId, stored.FamilyId);

            throw new UnauthorizedException("That session is no longer valid. Sign in again.");
        }

        if (!stored.IsActive(now))
        {
            throw new UnauthorizedException("That session has expired. Sign in again.");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, cancellationToken)
                   ?? throw new UnauthorizedException("That session is no longer valid. Sign in again.");

        var (result, issued) = await IssueAsync(user, stored.FamilyId, isNewUser: false, cancellationToken);

        // Spent, not deleted: the chain is what makes a later replay detectable.
        stored.RevokedAt = now;
        stored.RevokedReason = "Rotated.";
        stored.ReplacedByTokenId = issued.Id;

        await _db.SaveChangesAsync(cancellationToken);

        return result;
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var hash = HashRefreshToken(refreshToken);
        var stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (stored is null)
        {
            // Signing out with a token we do not recognise is not an error worth reporting: the
            // caller wanted to be signed out and they are. Saying otherwise only tells someone
            // probing which tokens exist.
            return;
        }

        await RevokeFamilyAsync(stored.FamilyId, _clock.UtcNow, "Signed out.", cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Mints an access token and a refresh token, both belonging to one sign-in family.</summary>
    private async Task<(AuthResult Result, RefreshToken Record)> IssueAsync(
        User user,
        Guid familyId,
        bool isNewUser,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var (accessToken, expiresAt) = _tokens.CreateAccessToken(user.Id, user.Role, user.Phone);

        var refreshToken = GenerateRefreshToken();
        var record = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = HashRefreshToken(refreshToken),
            FamilyId = familyId,
            ExpiresAt = now.AddDays(_options.RefreshTokenLifetimeDays),
            CreatedAt = now
        };

        _db.RefreshTokens.Add(record);
        await _db.SaveChangesAsync(cancellationToken);

        return (new AuthResult(
            accessToken,
            expiresAt,
            user.Id,
            user.Role,
            isNewUser,
            refreshToken,
            record.ExpiresAt), record);
    }

    private async Task RevokeFamilyAsync(
        Guid familyId,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        var family = await _db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in family)
        {
            token.RevokedAt = now;
            token.RevokedReason = reason;
        }
    }

    /// <summary>
    /// 256 bits of randomness. Unlike the OTP this is never typed by a human, so it can be as long
    /// as it likes and does not need a guessing cap to be safe.
    /// </summary>
    private static string GenerateRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

    /// <summary>
    /// Plain SHA-256, not the peppered HMAC the OTP uses. A six-digit code is brute-forceable from
    /// its hash and needs the secret to stop that; a 256-bit random string is not.
    /// </summary>
    private static string HashRefreshToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// Caps how much SMS a single number can cost us, on two axes: a cooldown between consecutive
    /// codes, and a ceiling over a rolling window.
    ///
    /// This is counted per phone number rather than per caller because the number is what costs
    /// money — an attacker rotating IPs still cannot make us send a sixth message to the same
    /// handset in an hour. Per-caller limiting sits in front of this at the HTTP edge; neither is
    /// sufficient alone.
    /// </summary>
    private async Task EnforceRequestLimitsAsync(string phone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var windowStart = now.AddMinutes(-_options.OtpRequestWindowMinutes);

        var recent = await _db.OtpCodes
            .Where(o => o.Phone == phone && o.CreatedAt >= windowStart)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => o.CreatedAt)
            .Take(_options.OtpMaxRequestsPerWindow)
            .ToListAsync(cancellationToken);

        if (recent.Count > 0)
        {
            var sinceLast = now - recent[0];
            var cooldown = TimeSpan.FromSeconds(_options.OtpResendCooldownSeconds);

            if (sinceLast < cooldown)
            {
                throw new TooManyRequestsException(
                    "A code was just sent. Wait a moment before asking for another.",
                    cooldown - sinceLast);
            }
        }

        if (recent.Count >= _options.OtpMaxRequestsPerWindow)
        {
            // recent is capped at the limit and newest-first, so the last element is the oldest
            // request still inside the window — the moment it ages out is when a slot frees up.
            var retryAfter = recent[^1].AddMinutes(_options.OtpRequestWindowMinutes) - now;

            throw new TooManyRequestsException(
                "Too many codes requested for this number. Try again later.",
                retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>Cryptographically random, so codes cannot be predicted from a previous one.</summary>
    private static string GenerateCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private string HashCode(string phone, string code)
    {
        var pepper = string.IsNullOrEmpty(_options.OtpPepper)
            ? throw new InvalidOperationException("Auth:OtpPepper is not configured.")
            : _options.OtpPepper;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(pepper));
        // Phone is bound into the hash so a code issued for one number cannot be replayed against another.
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{phone}:{code}"));
        return Convert.ToBase64String(hash);
    }

    private static string NormalisePhone(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            throw new DomainException("A phone number is required.");
        }

        var digits = new string(phone.Where(char.IsDigit).ToArray());

        if (digits.Length is < 10 or > 15)
        {
            throw new DomainException("That phone number does not look valid.");
        }

        return digits;
    }
}
