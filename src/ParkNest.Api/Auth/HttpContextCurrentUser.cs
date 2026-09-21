using System.Security.Claims;
using ParkNest.Application.Abstractions;
using ParkNest.Domain.Common;

namespace ParkNest.Api.Auth;

/// <summary>Resolves the caller from the validated bearer token on the current request.</summary>
public sealed class HttpContextCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextCurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public Guid? UserId
    {
        get
        {
            var raw = Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(raw, out var id) ? id : null;
        }
    }

    public UserRole? Role
    {
        get
        {
            var raw = Principal?.FindFirstValue(ClaimTypes.Role);
            return Enum.TryParse<UserRole>(raw, out var role) ? role : null;
        }
    }

    /// <summary>
    /// The "email" claim the token service puts on a token for an account that has one. Lower-cased
    /// here as well as at sign-in, so a comparison against a configured address cannot turn on how
    /// somebody typed it.
    /// </summary>
    public string? Email
    {
        get
        {
            var raw = Principal?.FindFirstValue("email")?.Trim();
            return string.IsNullOrEmpty(raw) ? null : raw.ToLowerInvariant();
        }
    }

    public bool IsAuthenticated => UserId.HasValue;

    public Guid RequireUserId() => UserId ?? throw new UnauthorizedException();

    public void RequireSelfOrAdmin(Guid userId)
    {
        var caller = RequireUserId();

        if (caller == userId || Role == UserRole.Admin)
        {
            return;
        }

        // Same message whether the resource exists or not — a distinct "not found" here would let
        // a caller enumerate which user ids are real.
        throw new ForbiddenException();
    }

    public void RequireAdmin()
    {
        RequireUserId();

        if (Role != UserRole.Admin)
        {
            throw new ForbiddenException("This action requires the platform admin role.");
        }
    }
}
