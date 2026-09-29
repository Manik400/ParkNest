using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;
using ParkNest.Infrastructure.Auth;

namespace ParkNest.Infrastructure.Email;

/// <summary>Sends an email over plain SMTP, with the same server settings as the sign-in codes.</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        using var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromName, _options.SenderAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));

        if (!string.IsNullOrWhiteSpace(message.ReplyTo))
        {
            mime.ReplyTo.Add(new MailboxAddress(message.ReplyToName ?? string.Empty, message.ReplyTo));
        }

        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.Text, HtmlBody = message.Html }.ToMessageBody();

        using var client = new SmtpClient();

        try
        {
            await client.ConnectAsync(
                _options.Host, _options.Port, SmtpEmailOtpSender.SecurityFor(_options.Security), cancellationToken);

            if (!string.IsNullOrWhiteSpace(_options.Username))
            {
                await client.AuthenticateAsync(_options.Username, _options.Password, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "The SMTP server at {Host}:{Port} did not accept an email to {Recipient}.",
                _options.Host, _options.Port, message.To);

            throw new DomainException("Your message could not be sent just now. Please try again shortly.");
        }
    }
}
