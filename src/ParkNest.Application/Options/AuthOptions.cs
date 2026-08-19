namespace ParkNest.Application.Options;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// HMAC signing key for access tokens. Must be at least 32 bytes and must come from a secret
    /// store in production — the value in appsettings.json is a development placeholder and the
    /// API refuses to start with it outside Development.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "parknest";
    public string Audience { get; set; } = "parknest-clients";

    /// <summary>
    /// Short, because a signed JWT cannot be withdrawn — there is no server-side state to revoke,
    /// so the only real control over a stolen one is that it expires. Session length lives on the
    /// refresh token instead, which is stored and therefore revocable.
    /// </summary>
    public int AccessTokenLifetimeMinutes { get; set; } = 60;

    /// <summary>How long a session survives without the user signing in again.</summary>
    public int RefreshTokenLifetimeDays { get; set; } = 30;

    public int OtpLifetimeMinutes { get; set; } = 5;

    /// <summary>Wrong guesses allowed before a code is burned.</summary>
    public int OtpMaxAttempts { get; set; } = 5;

    /// <summary>
    /// Shortest gap between two code requests for the same number. Stops a "resend" button held
    /// down from becoming an SMS bill.
    /// </summary>
    public int OtpResendCooldownSeconds { get; set; } = 60;

    /// <summary>Codes a single number may be sent within <see cref="OtpRequestWindowMinutes"/>.</summary>
    public int OtpMaxRequestsPerWindow { get; set; } = 5;

    /// <summary>The rolling window the request cap is counted over.</summary>
    public int OtpRequestWindowMinutes { get; set; } = 60;

    /// <summary>Server-side key for hashing OTPs at rest. Must also come from a secret store.</summary>
    public string OtpPepper { get; set; } = string.Empty;

    /// <summary>Phone numbers permitted to mint an admin token. Empty in production deployments.</summary>
    public string[] AdminPhones { get; set; } = Array.Empty<string>();
}
