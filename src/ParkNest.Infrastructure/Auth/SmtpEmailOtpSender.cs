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

        // The code leads the subject so it can be read off the notification without opening the
        // mail, and so iOS and Android offer it as a one-tap autofill.
        message.Subject = $"{code} is your ParkNest sign-in code";

        var body = new BodyBuilder
        {
            TextBody =
                $"Your ParkNest sign-in code is {code}.\n\n" +
                $"It expires in {_lifetimeMinutes} minutes. If you did not ask for it, ignore this email - " +
                "nobody can sign in without the code.",

            HtmlBody = $$"""
                <div style="font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;max-width:420px;margin:0 auto;padding:24px;color:#1f2933">
                  <p style="margin:0 0 16px">Your ParkNest sign-in code is</p>
                  <p style="margin:0 0 16px;font-size:32px;font-weight:700;letter-spacing:6px">{{code}}</p>
                  <p style="margin:0;color:#52606d;font-size:14px">It expires in {{_lifetimeMinutes}} minutes. If you did not ask for it, ignore this email &mdash; nobody can sign in without the code.</p>
                </div>
                """
        };

        message.Body = body.ToMessageBody();

        return message;
    }
}
