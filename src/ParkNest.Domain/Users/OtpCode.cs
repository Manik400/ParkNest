namespace ParkNest.Domain.Users;

/// <summary>
/// A pending phone or email verification. The code itself is never stored — only a keyed hash —
/// so a database leak does not hand out live login codes.
/// </summary>
public class OtpCode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Normalised phone digits or a lower-cased email address. The two cannot collide: only an
    /// address contains '@'.
    /// </summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>HMAC of the code, keyed with a server-side pepper.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>Wrong guesses so far. Caps brute-force against a 6-digit space.</summary>
    public int AttemptCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsConsumed => ConsumedAt.HasValue;

    public bool IsUsable(DateTimeOffset now, int maxAttempts) =>
        !IsConsumed && now < ExpiresAt && AttemptCount < maxAttempts;
}
