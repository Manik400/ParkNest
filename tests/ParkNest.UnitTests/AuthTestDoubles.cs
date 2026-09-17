using ParkNest.Application.Abstractions;
using ParkNest.Application.Auth;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

/// <summary>Captures the code that would have been texted or emailed, so tests can read it back.</summary>
public sealed class RecordingOtpSender : IOtpSender
{
    public RecordingOtpSender(OtpChannel channel = OtpChannel.Sms) => Channel = channel;

    public OtpChannel Channel { get; }

    public List<(string Destination, string Code)> Sent { get; } = new();

    public string? LastCode => Sent.Count == 0 ? null : Sent[^1].Code;

    public bool ExposesCodeInResponse => true;

    public Task SendAsync(string destination, string code, CancellationToken cancellationToken = default)
    {
        Sent.Add((destination, code));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Token signing is exercised by the real JwtTokenService in the API; these tests care about the
/// OTP state machine, so the token is just an opaque string here.
/// </summary>
public sealed class FakeTokenService : ITokenService
{
    // The harness clock, not the wall clock: the refresh expiry is stamped from the harness clock,
    // and comparing the two stopped working once the frozen origin fell a month behind real time.
    private readonly IClock _clock;

    public FakeTokenService(IClock clock) => _clock = clock;

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(Guid userId, UserRole role, string? phone, string? email) =>
        ($"test-token:{userId}:{role}", _clock.UtcNow.AddHours(12));
}
