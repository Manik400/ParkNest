namespace ParkNest.Domain.Common;

/// <summary>No valid credentials on the request. Maps to HTTP 401.</summary>
public sealed class UnauthorizedException : Exception
{
    public UnauthorizedException(string message = "Authentication is required.") : base(message) { }
}

/// <summary>
/// Authenticated, but not allowed to touch this resource. Maps to HTTP 403.
/// Deliberately distinct from <see cref="DomainException"/>: a permission failure is not a
/// business-rule failure, and conflating them leaks resource existence through error messages.
/// </summary>
public sealed class ForbiddenException : Exception
{
    public ForbiddenException(string message = "You do not have access to this resource.") : base(message) { }
}
