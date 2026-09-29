using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;

namespace ParkNest.Infrastructure.Email;

/// <summary>
/// Sends an email over Brevo's HTTPS API, from the same verified sender and key as the sign-in
/// codes. Port 443 only, so it works on Render's free plan, which blocks SMTP.
/// </summary>
public sealed class BrevoEmailSender : IEmailSender
{
    private const string Endpoint = "https://api.brevo.com/v3/smtp/email";

    private readonly HttpClient _http;
    private readonly EmailOptions _options;
    private readonly ILogger<BrevoEmailSender> _logger;

    public BrevoEmailSender(HttpClient http, IOptions<EmailOptions> options, ILogger<BrevoEmailSender> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.Remove("api-key");
        _http.DefaultRequestHeaders.Add("api-key", _options.ApiKey);
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object>
        {
            ["sender"] = new { name = _options.FromName, email = _options.SenderAddress },
            ["to"] = new[] { new { email = message.To } },
            ["subject"] = message.Subject,
            ["textContent"] = message.Text,
            ["htmlContent"] = message.Html
        };

        if (!string.IsNullOrWhiteSpace(message.ReplyTo))
        {
            payload["replyTo"] = string.IsNullOrWhiteSpace(message.ReplyToName)
                ? new { email = message.ReplyTo }
                : new { email = message.ReplyTo, name = message.ReplyToName };
        }

        try
        {
            using var response = await _http.PostAsJsonAsync(Endpoint, payload, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Brevo rejected an email to {Recipient}: {Status} {Body}",
                message.To, (int)response.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Brevo could not be reached for an email to {Recipient}.", message.To);
        }

        throw new DomainException("Your message could not be sent just now. Please try again shortly.");
    }
}
