using ParkNest.Domain.Common;

namespace ParkNest.Application.Abstractions;

/// <summary>
/// The authenticated caller, resolved from the bearer token. Application services depend on this
/// rather than on an id passed in from the request body — a body-supplied user id is a caller
/// *claim*, not an authenticated fact, and trusting one lets anybody spend anybody's credits.
/// </summary>
public interface ICurrentUser
{
    /// <summary>Null when the request is unauthenticated.</summary>
    Guid? UserId { get; }

    UserRole? Role { get; }

    /// <summary>
    /// The address the caller signed in with, lower-cased, or null for an account created by
    /// phone. Read only where a check is against a named person rather than a role — the
    /// analytics allow-list — and never as a substitute for <see cref="UserId"/>.
    /// </summary>
    string? Email { get; }

    bool IsAuthenticated { get; }

    /// <summary>The caller's id, or <see cref="UnauthorizedException"/> if there isn't one.</summary>
    Guid RequireUserId();

    /// <summary>Throws unless the caller is <paramref name="userId"/> or an admin.</summary>
    void RequireSelfOrAdmin(Guid userId);

    /// <summary>Throws unless the caller holds the platform admin role.</summary>
    void RequireAdmin();
}
