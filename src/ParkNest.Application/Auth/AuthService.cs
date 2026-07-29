using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
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

    public AuthService(
        IParkNestDbContext db,
        ITokenService tokens,
        IOtpSender otpSender,
        IClock clock,
        IOptions<AuthOptions> options)
    {
        _db = db;
        _tokens = tokens;
        _otpSender = otpSender;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<OtpChallenge> RequestOtpAsync(string phone, CancellationToken cancellationToken = default)
    {
        phone = NormalisePhone(phone);

        var now = _clock.UtcNow;
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

        var (token, expiresAt) = _tokens.CreateAccessToken(user.Id, user.Role, user.Phone);

        return new AuthResult(token, expiresAt, user.Id, user.Role, isNewUser);
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
