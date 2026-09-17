using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ParkNest.Application.Auth;

namespace ParkNest.Infrastructure.Auth;

/// <summary>
/// Development stand-in for a real sender, on either channel: writes the code to the log and
/// returns it in the API response so the app can be driven without a mail or SMS account.
///
/// Registration is guarded by environment — see <c>DependencyInjection</c>, which refuses to wire
/// this in Production. Shipping a login that prints its own OTP would be a total bypass.
/// </summary>
public sealed class LoggingOtpSender : IOtpSender
{
    private readonly ILogger<LoggingOtpSender> _logger;

    public LoggingOtpSender(OtpChannel channel, ILogger<LoggingOtpSender> logger, IHostEnvironment environment)
    {
        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "LoggingOtpSender must never be used in Production. Configure a real email or SMS sender.");
        }

        Channel = channel;
        _logger = logger;
    }

    public OtpChannel Channel { get; }

    public bool ExposesCodeInResponse => true;

    public Task SendAsync(string destination, string code, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("DEV OTP ({Channel}) for {Destination}: {Code}", Channel, destination, code);
        return Task.CompletedTask;
    }
}
