using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;
using ParkNest.Infrastructure.Auth;

namespace ParkNest.UnitTests;

/// <summary>
/// The email path, run through the real MailKit client against a minimal SMTP server in this
/// process — so what is proven is the conversation a real server would see, not a mock of it.
///
/// As with the SMS gateway, the boundary is what matters: the code reaches exactly one inbox, and
/// a server that cannot be reached arrives as a domain failure the caller can show, never a 500.
/// </summary>
public sealed class SmtpEmailOtpSenderTests
{
    private static SmtpEmailOtpSender Sender(int port) =>
        new(Microsoft.Extensions.Options.Options.Create(new EmailOptions
            {
                Provider = "Smtp",
                Host = "127.0.0.1",
                Port = port,
                Security = "None",
                FromAddress = "noreply@parknest.test"
            }),
            Microsoft.Extensions.Options.Options.Create(new AuthOptions { OtpLifetimeMinutes = 5 }),
            NullLogger<SmtpEmailOtpSender>.Instance);

    [Fact]
    public async Task Sends_the_code_to_one_recipient_with_the_code_in_the_subject()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = ServeOneMessageAsync(listener, timeout.Token);

            await Sender(((IPEndPoint)listener.LocalEndpoint).Port).SendAsync("renter@example.com", "482915");

            var transcript = await server;

            transcript.Should().Contain("MAIL FROM:<noreply@parknest.test>");
            transcript.Should().Contain("RCPT TO:<renter@example.com>");
            transcript.Split("RCPT TO:").Should().HaveCount(2, "the code goes to exactly one inbox");
            transcript.Should().Contain("Subject: 482915 is your ParkNest sign-in code");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task An_unreachable_server_is_a_domain_failure_not_a_crash()
    {
        // A port nothing listens on: bind one, note it, release it.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var act = () => Sender(port).SendAsync("renter@example.com", "123456");

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public void Gmail_with_an_app_password_is_configured_and_sends_from_the_account()
    {
        var options = new EmailOptions
        {
            Provider = "Smtp", Host = "smtp.gmail.com", Username = "me@gmail.com", Password = "app-password"
        };

        options.IsSmtpConfigured.Should().BeTrue();
        options.SenderAddress.Should().Be("me@gmail.com");
    }

    [Fact]
    public void A_username_without_a_password_is_a_typo_not_a_configuration()
    {
        var options = new EmailOptions { Provider = "Smtp", Host = "smtp.gmail.com", Username = "me@gmail.com" };

        options.IsSmtpConfigured.Should().BeFalse();
    }

    [Fact]
    public void A_mail_catcher_needs_no_credentials()
    {
        var options = new EmailOptions
        {
            Provider = "Smtp", Host = "localhost", Port = 25, Security = "None", FromAddress = "noreply@parknest.test"
        };

        options.IsSmtpConfigured.Should().BeTrue();
    }

    [Fact]
    public void Opportunistic_tls_is_not_accepted()
    {
        // StartTlsWhenAvailable would send the password in the clear to a network that stripped
        // the STARTTLS offer. It is a real MailKit mode, which is exactly why it is refused by name.
        var options = new EmailOptions
        {
            Provider = "Smtp", Host = "smtp.gmail.com", Username = "me@gmail.com", Password = "x",
            Security = "StartTlsWhenAvailable"
        };

        options.IsSmtpConfigured.Should().BeFalse();
    }

    /// <summary>Just enough SMTP to accept one message and hand back everything the client said.</summary>
    private static async Task<string> ServeOneMessageAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        var transcript = new StringBuilder();
        await writer.WriteLineAsync("220 localhost ESMTP test");

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            transcript.AppendLine(line);

            switch (line.Split(' ', ':')[0].ToUpperInvariant())
            {
                case "DATA":
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");

                    while (await reader.ReadLineAsync(cancellationToken) is { } data && data != ".")
                    {
                        transcript.AppendLine(data);
                    }

                    await writer.WriteLineAsync("250 Queued");
                    break;

                case "QUIT":
                    await writer.WriteLineAsync("221 Bye");
                    return transcript.ToString();

                default:
                    // EHLO, MAIL FROM, RCPT TO: accept everything, advertise nothing.
                    await writer.WriteLineAsync("250 OK");
                    break;
            }
        }

        return transcript.ToString();
    }
}
