using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Analytics;
using ParkNest.Application.Options;
using ParkNest.Domain.Analytics;
using ParkNest.Domain.Common;
using ParkNest.Domain.Users;

namespace ParkNest.Application.Auth;

public sealed class AuthService : IAuthService
{
    private readonly IParkNestDbContext _db;
    private readonly ITokenService _tokens;
    private readonly IReadOnlyDictionary<OtpChannel, IOtpSender> _senders;
    private readonly IClock _clock;
    private readonly AuthOptions _options;
    private readonly IAnalyticsRecorder _analytics;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IParkNestDbContext db,
        ITokenService tokens,
        IEnumerable<IOtpSender> otpSenders,
        IClock clock,
        IOptions<AuthOptions> options,
        IAnalyticsRecorder analytics,
        ILogger<AuthService> logger)
    {
        _db = db;
        _tokens = tokens;
        // Throws on two senders for one channel, which is a wiring mistake worth failing loudly on.
        _senders = otpSenders.ToDictionary(s => s.Channel);
        _clock = clock;
        _options = options.Value;
        _analytics = analytics;
        _logger = logger;
    }

    public async Task<OtpChallenge> RequestOtpAsync(string destination, CancellationToken cancellationToken = default)
    {
        var target = OtpDestination.Parse(destination);
        var sender = SenderFor(target.Channel);

        var now = _clock.UtcNow;

        await EnforceRequestLimitsAsync(target.Value, now, cancellationToken);

        var code = GenerateCode();

        // Any earlier live code for this destination is burned, so requesting a new one cannot be
        // used to widen the guessing window on the old one.
        var outstanding = await _db.OtpCodes
            .Where(o => o.Destination == target.Value && o.ConsumedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var stale in outstanding)
        {
            stale.ConsumedAt = now;
        }

        var otp = new OtpCode
        {
            Destination = target.Value,
            CodeHash = HashCode(target.Value, code),
            ExpiresAt = now.AddMinutes(_options.OtpLifetimeMinutes),
            CreatedAt = now
        };

        _db.OtpCodes.Add(otp);
        await _db.SaveChangesAsync(cancellationToken);

        await sender.SendAsync(target.Value, code, cancellationToken);

        // The channel, never the destination. An email address in the analytics table would make
        // it a list of everyone who ever tried to sign in, which is the one thing it must not be.
        await _analytics.RecordAsync(
            new AnalyticsHit(
                AnalyticsEventNames.SignInRequested,
                Detail: target.Channel.ToString()),
            cancellationToken);

        return new OtpChallenge(otp.ExpiresAt, sender.ExposesCodeInResponse ? code : null);
    }

    public async Task<AuthResult> VerifyOtpAsync(string destination, string code, CancellationToken cancellationToken = default)
    {
        var target = OtpDestination.Parse(destination);
        var now = _clock.UtcNow;

        var otp = await _db.OtpCodes
            .Where(o => o.Destination == target.Value && o.ConsumedAt == null)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        // One message for every failure mode: wrong code, expired code, no code requested. Telling
        // the caller which one it was hands an attacker a probe for registered numbers and addresses.
        if (otp is null || !otp.IsUsable(now, _options.OtpMaxAttempts))
        {
            throw new UnauthorizedException("That code is not valid. Request a new one.");
        }

        var expected = HashCode(target.Value, code);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(otp.CodeHash),
                Encoding.UTF8.GetBytes(expected)))
        {
            otp.AttemptCount++;
            await _db.SaveChangesAsync(cancellationToken);
            throw new UnauthorizedException("That code is not valid. Request a new one.");
        }

        otp.ConsumedAt = now;

        var user = target.Channel == OtpChannel.Email
            ? await _db.Users.FirstOrDefaultAsync(u => u.Email == target.Value, cancellationToken)
            : await _db.Users.FirstOrDefaultAsync(u => u.Phone == target.Value, cancellationToken);
        var isNewUser = user is null;

        if (user is null)
        {
            user = new User
            {
                Phone = target.Channel == OtpChannel.Sms ? target.Value : null,
                Email = target.Channel == OtpChannel.Email ? target.Value : null,
                FullName = string.Empty,
                // Both, because a user is free to list a space and rent one; the API authorises
                // per action, not by locking the account into a single side of the marketplace.
                Role = IsAdmin(target) ? UserRole.Admin : UserRole.Both,
                CreatedAt = now
            };
            _db.Users.Add(user);
        }
        else if (IsAdmin(target) && user.Role != UserRole.Admin)
        {
            // The list is read on every sign-in, not only at the moment an account is created.
            // Without this, adding your own address to Auth:AdminEmails does nothing whenever you
            // had already signed in once before — which is exactly when somebody adds it.
            //
            // One direction only: removing an address does not demote, because the role is also
            // settable by hand and a config change must not quietly undo that.
            _logger.LogInformation("Promoting {UserId} to admin: the destination is on the admin list.", user.Id);
            user.Role = UserRole.Admin;
        }

        await _db.SaveChangesAsync(cancellationToken);

        var (result, _) = await IssueAsync(user, Guid.NewGuid(), isNewUser, cancellationToken);

        await _analytics.RecordAsync(
            new AnalyticsHit(
                isNewUser ? AnalyticsEventNames.SignedUp : AnalyticsEventNames.SignedIn,
                UserId: user.Id,
                Detail: target.Channel.ToString()),
            cancellationToken);

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
        var (accessToken, expiresAt) = _tokens.CreateAccessToken(user.Id, user.Role, user.Phone, user.Email);

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
    /// The sender for a channel, or a plain refusal when the deployment has none. Phone sign-in is
    /// off wherever no SMS provider is paid for, and saying so beats a code that never arrives.
    /// </summary>
    private IOtpSender SenderFor(OtpChannel channel) =>
        _senders.TryGetValue(channel, out var sender)
            ? sender
            : throw new DomainException(channel == OtpChannel.Sms
                ? "Signing in with a phone number is not available yet. Use your email address instead."
                : "Signing in with email is not available right now. Use your phone number instead.");

    private bool IsAdmin(OtpDestination target) =>
        target.Channel == OtpChannel.Email
            ? _options.AdminEmails.Any(e => string.Equals(e.Trim(), target.Value, StringComparison.OrdinalIgnoreCase))
            : _options.AdminPhones.Contains(target.Value);

    /// <summary>
    /// Caps how many codes a single destination can be sent, on two axes: a cooldown between
    /// consecutive codes, and a ceiling over a rolling window.
    ///
    /// This is counted per destination rather than per caller because the destination is what gets
    /// flooded — and, over SMS, what costs money. An attacker rotating IPs still cannot make us send
    /// a sixth code to the same inbox or handset in an hour. Per-caller limiting sits in front of
    /// this at the HTTP edge; neither is sufficient alone.
    /// </summary>
    private async Task EnforceRequestLimitsAsync(string destination, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var windowStart = now.AddMinutes(-_options.OtpRequestWindowMinutes);

        var recent = await _db.OtpCodes
            .Where(o => o.Destination == destination && o.CreatedAt >= windowStart)
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
                "Too many codes requested for this address. Try again later.",
                retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>Cryptographically random, so codes cannot be predicted from a previous one.</summary>
    private static string GenerateCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private string HashCode(string destination, string code)
    {
        var pepper = string.IsNullOrEmpty(_options.OtpPepper)
            ? throw new InvalidOperationException("Auth:OtpPepper is not configured.")
            : _options.OtpPepper;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(pepper));
        // The destination is bound into the hash so a code issued for one number or address cannot
        // be replayed against another.
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{destination}:{code}"));
        return Convert.ToBase64String(hash);
    }
}
