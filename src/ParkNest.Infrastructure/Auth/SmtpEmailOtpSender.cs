using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using ParkNest.Application.Auth;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;

namespace ParkNest.Infrastructure.Auth;

/// <summary>
/// Sends the one-time code by email over plain SMTP — the sign-in channel that costs nothing at
/// this scale. Gmail with an App Password gives about 500 messages a day, Brevo's free relay 300,
/// and a local mail catcher (smtp4dev, Mailpit) as many as you like without anything leaving the
/// machine. All three are this one class, because SMTP is the contract rather than any vendor.
///
/// Unlike SMS, nothing here is billed or regulated per message, which is why email carries
/// sign-in until the platform pays for an SMS provider.
/// </summary>
public sealed class SmtpEmailOtpSender : IOtpSender
{
    private readonly EmailOptions _options;
    private readonly int _lifetimeMinutes;
    private readonly ILogger<SmtpEmailOtpSender> _logger;

    public SmtpEmailOtpSender(
        IOptions<EmailOptions> options,
        IOptions<AuthOptions> auth,
        ILogger<SmtpEmailOtpSender> logger)
    {
        _options = options.Value;
        _lifetimeMinutes = auth.Value.OtpLifetimeMinutes;
        _logger = logger;
    }

    public OtpChannel Channel => OtpChannel.Email;

    /// <summary>Never — the code goes to the inbox and nowhere else.</summary>
    public bool ExposesCodeInResponse => false;

    public async Task SendAsync(string destination, string code, CancellationToken cancellationToken = default)
    {
        using var message = BuildMessage(destination, code);
        using var client = new SmtpClient();

        try
        {
            await client.ConnectAsync(_options.Host, _options.Port, SecurityFor(_options.Security), cancellationToken);

            if (!string.IsNullOrWhiteSpace(_options.Username))
            {
                await client.AuthenticateAsync(_options.Username, _options.Password, cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A revoked App Password, Gmail's daily cap, a dropped connection: all logged with the
            // server's own reason, none of it (and never the code) repeated to the caller.
            _logger.LogError(
                ex,
                "The SMTP server at {Host}:{Port} did not accept an OTP email to {Recipient}.",
                _options.Host, _options.Port, destination);

            throw new DomainException("Could not send the verification code. Please try again shortly.");
        }
    }

    internal static SecureSocketOptions SecurityFor(string security) => security.ToLowerInvariant() switch
    {
        "starttls" => SecureSocketOptions.StartTls,
        "sslonconnect" => SecureSocketOptions.SslOnConnect,
        "none" => SecureSocketOptions.None,
        _ => throw new InvalidOperationException(
            $"Email:Security '{security}' is not one of StartTls, SslOnConnect or None.")
    };

    private MimeMessage BuildMessage(string destination, string code)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.SenderAddress));
        message.To.Add(MailboxAddress.Parse(destination));

        message.Subject = OtpEmail.Subject(code);

        var body = new BodyBuilder
        {
            TextBody = OtpEmail.Text(code, _lifetimeMinutes),
            HtmlBody = OtpEmail.Html(code, _lifetimeMinutes)
        };

        message.Body = body.ToMessageBody();

        return message;
    }
}
