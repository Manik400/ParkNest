namespace ParkNest.Domain.Users;

/// <summary>
/// A long-lived credential that buys short-lived access tokens.
///
/// It exists so access tokens do not have to. A JWT cannot be withdrawn once signed — there is no
/// server-side state to revoke — so the only real control over a stolen one is that it expires
/// soon. Pushing session length onto a token we *do* store makes that affordable: sessions last
/// weeks, access tokens last an hour, and signing out actually ends something.
///
/// Stored as a hash, like the OTP: a database leak must not hand out live sessions.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the token. The token itself is only ever seen by the client.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Why it was revoked — rotated, signed out, or replayed. Useful when reading an incident.</summary>
    public string? RevokedReason { get; set; }

    /// <summary>
    /// The token issued in its place when it was used. Rotation leaves a chain, and the chain is
    /// what makes replay detectable: a token that already has a successor is one somebody kept a
    /// copy of.
    /// </summary>
    public Guid? ReplacedByTokenId { get; set; }

    /// <summary>
    /// Groups every token descended from one sign-in. Detecting a replay revokes the whole family,
    /// because once a token has leaked there is no telling which side of the chain is the thief.
    /// </summary>
    public Guid FamilyId { get; set; } = Guid.NewGuid();

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;
}
