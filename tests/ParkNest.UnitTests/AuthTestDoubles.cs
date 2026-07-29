using ParkNest.Application.Auth;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

/// <summary>Captures the code that would have been texted, so tests can read it back.</summary>
public sealed class RecordingOtpSender : IOtpSender
{
    public List<(string Phone, string Code)> Sent { get; } = new();

    public string? LastCode => Sent.Count == 0 ? null : Sent[^1].Code;

    public bool ExposesCodeInResponse => true;

    public Task SendAsync(string phone, string code, CancellationToken cancellationToken = default)
    {
        Sent.Add((phone, code));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Token signing is exercised by the real JwtTokenService in the API; these tests care about the
/// OTP state machine, so the token is just an opaque string here.
/// </summary>
public sealed class FakeTokenService : ITokenService
{
    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(Guid userId, UserRole role, string phone) =>
        ($"test-token:{userId}:{role}", DateTimeOffset.UtcNow.AddHours(12));
}
