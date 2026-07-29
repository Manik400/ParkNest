using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ParkNest.Application.Auth;

namespace ParkNest.Infrastructure.Auth;

/// <summary>
/// Development stand-in for an SMS gateway: writes the code to the log and returns it in the API
/// response so the app can be driven without a provider account.
///
/// Registration is guarded by environment — see <c>DependencyInjection</c>, which refuses to wire
/// this outside Development. Shipping a login that prints its own OTP would be a total bypass.
/// </summary>
public sealed class LoggingOtpSender : IOtpSender
{
    private readonly ILogger<LoggingOtpSender> _logger;

    public LoggingOtpSender(ILogger<LoggingOtpSender> logger, IHostEnvironment environment)
    {
        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "LoggingOtpSender must never be used in Production. Configure a real SMS gateway.");
        }

        _logger = logger;
    }

    public bool ExposesCodeInResponse => true;

    public Task SendAsync(string phone, string code, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("DEV OTP for {Phone}: {Code}", phone, code);
        return Task.CompletedTask;
    }
}
