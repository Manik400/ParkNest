using ParkNest.Application.Abstractions;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

/// <summary>
/// Stands in for the bearer token in tests. <see cref="SignIn"/> switches the acting user, which
/// is how the authorisation tests check that one user cannot act on another's resources.
/// </summary>
public sealed class TestCurrentUser : ICurrentUser
{
    public Guid? UserId { get; private set; }
    public UserRole? Role { get; private set; }
    public string? Email { get; private set; }

    public bool IsAuthenticated => UserId.HasValue;

    public void SignIn(Guid userId, UserRole role = UserRole.Both, string? email = null)
    {
        UserId = userId;
        Role = role;
        Email = email?.ToLowerInvariant();
    }

    public void SignOut()
    {
        UserId = null;
        Role = null;
        Email = null;
    }

    public Guid RequireUserId() => UserId ?? throw new UnauthorizedException();

    public void RequireSelfOrAdmin(Guid userId)
    {
        var caller = RequireUserId();
        if (caller != userId && Role != UserRole.Admin)
        {
            throw new ForbiddenException();
        }
    }

    public void RequireAdmin()
    {
        RequireUserId();
        if (Role != UserRole.Admin)
        {
            throw new ForbiddenException();
        }
    }
}
