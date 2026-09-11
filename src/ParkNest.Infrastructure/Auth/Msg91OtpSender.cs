using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Auth;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;

namespace ParkNest.Infrastructure.Auth;

/// <summary>
/// Sends the one-time code over SMS via MSG91, chosen for Indian DLT compliance — domestic
/// carriers drop transactional SMS that does not match a registered template.
/// </summary>
public sealed class Msg91OtpSender : IOtpSender
{
    private const string Endpoint = "https://control.msg91.com/api/v5/flow/";

    private readonly HttpClient _http;
    private readonly SmsOptions _options;
    private readonly ILogger<Msg91OtpSender> _logger;

    public Msg91OtpSender(HttpClient http, IOptions<SmsOptions> options, ILogger<Msg91OtpSender> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        _http.DefaultRequestHeaders.Remove("authkey");
        _http.DefaultRequestHeaders.Add("authkey", _options.AuthKey);
    }

    public OtpChannel Channel => OtpChannel.Sms;

    /// <summary>Never — the code goes to the handset and nowhere else.</summary>
    public bool ExposesCodeInResponse => false;

    public async Task SendAsync(string phone, string code, CancellationToken cancellationToken = default)
    {
        // MSG91 expects the country code inline. Ten digits means a bare Indian number.
        var recipient = phone.Length == 10 ? "91" + phone : phone;

        var payload = JsonSerializer.Serialize(new
        {
            template_id = _options.TemplateId,
            sender = _options.SenderId,
            short_url = "0",
            recipients = new[] { new { mobiles = recipient, otp = code } }
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(Endpoint, content, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        // Log the provider's reason but never the code, and give the user a message that does not
        // leak our provider or its error shape.
        _logger.LogError(
            "MSG91 rejected an OTP send to {Recipient}: {Status} {Body}",
            recipient, (int)response.StatusCode, body);

        throw new DomainException("Could not send the verification code. Please try again shortly.");
    }
}
