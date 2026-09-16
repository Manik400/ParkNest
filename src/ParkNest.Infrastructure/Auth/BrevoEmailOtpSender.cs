using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Auth;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;

namespace ParkNest.Infrastructure.Auth;

/// <summary>
/// Sends the one-time code through Brevo's HTTPS email API. Hosts that block outbound SMTP ports
/// (Render's free plan blocks 25, 465 and 587) can still reach port 443. Free: 300 emails a day.
/// </summary>
public sealed class BrevoEmailOtpSender : IOtpSender
{
    private const string Endpoint = "https://api.brevo.com/v3/smtp/email";

    private readonly HttpClient _http;
    private readonly EmailOptions _options;
    private readonly int _lifetimeMinutes;
    private readonly ILogger<BrevoEmailOtpSender> _logger;

    public BrevoEmailOtpSender(
        HttpClient http,
        IOptions<EmailOptions> options,
        IOptions<AuthOptions> auth,
        ILogger<BrevoEmailOtpSender> logger)
    {
        _http = http;
        _options = options.Value;
        _lifetimeMinutes = auth.Value.OtpLifetimeMinutes;
        _logger = logger;

        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.Remove("api-key");
        _http.DefaultRequestHeaders.Add("api-key", _options.ApiKey);
    }

    public OtpChannel Channel => OtpChannel.Email;

    /// <summary>Never — the code goes to the inbox and nowhere else.</summary>
    public bool ExposesCodeInResponse => false;

    public async Task SendAsync(string destination, string code, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            sender = new { name = _options.FromName, email = _options.SenderAddress },
            to = new[] { new { email = destination } },
            subject = OtpEmail.Subject(code),
            textContent = OtpEmail.Text(code, _lifetimeMinutes),
            htmlContent = OtpEmail.Html(code, _lifetimeMinutes)
        };

        try
        {
            using var response = await _http.PostAsJsonAsync(Endpoint, payload, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Brevo rejected an OTP email to {Recipient}: {Status} {Body}",
                destination, (int)response.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Brevo could not be reached for an OTP email to {Recipient}.", destination);
        }

        throw new DomainException("Could not send the verification code. Please try again shortly.");
    }
}
