using Microsoft.Extensions.Logging;
using ParkNest.Application.Abstractions;

namespace ParkNest.Infrastructure.Email;

/// <summary>
/// Development's stand-in: prints the email to the console instead of sending it, so the feedback
/// form works on a laptop with no mail account.
/// </summary>
public sealed class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger) => _logger = logger;

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "DEV EMAIL to {To} (reply-to {ReplyTo}): {Subject}\n{Text}",
            message.To, message.ReplyTo ?? "-", message.Subject, message.Text);
        return Task.CompletedTask;
    }
}
