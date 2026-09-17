using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Auth;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;

namespace ParkNest.Infrastructure.Auth;

/// <summary>
/// Sends the one-time code through a self-hosted SMS gateway rather than a commercial aggregator.
///
/// The gateway this speaks to is an ordinary Android handset with a SIM in it, running the
/// open-source SMS Gateway for Android (AGPL-3.0), which exposes <c>POST {BaseUrl}/message</c>
/// behind HTTP Basic auth. Anything presenting that shape works — a GSM modem fronted by Gammu
/// does too — because the contract here is one endpoint and two fields.
///
/// Why it exists: there is no such thing as a free software package that *delivers* SMS. Delivery
/// is a licensed telecom function, so every "SMS library" is an HTTP client for somebody's paid
/// account. Owning the last hop instead — a handset, its own SIM, its own message allowance — is
/// the only genuinely free option, and it is a real one for development and a small pilot.
///
/// It is emphatically not the answer at volume: one SIM sends a few hundred messages a day before
/// the carrier treats it as spam, there is no delivery receipt worth the name, and Indian
/// regulation puts commercial transactional SMS on DLT-registered headers that a personal SIM does
/// not have. <see cref="Msg91OtpSender"/> stays the production path.
/// </summary>
public sealed class SmsGatewayOtpSender : IOtpSender
{
    private readonly HttpClient _http;
    private readonly SmsOptions _options;
    private readonly ILogger<SmsGatewayOtpSender> _logger;

    public SmsGatewayOtpSender(
        HttpClient http,
        IOptions<SmsOptions> options,
        ILogger<SmsGatewayOtpSender> logger)
    {
        _options = options.Value;
        _logger = logger;

        _http = http;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");

        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}"));

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", credentials);
    }

    public OtpChannel Channel => OtpChannel.Sms;

    /// <summary>Never — the code goes to the handset and nowhere else.</summary>
    public bool ExposesCodeInResponse => false;

    public async Task SendAsync(string phone, string code, CancellationToken cancellationToken = default)
    {
        var recipient = ToE164(phone);

        var payload = JsonSerializer.Serialize(new
        {
            message = _options.MessageTemplate.Replace("{code}", code, StringComparison.Ordinal),
            phoneNumbers = new[] { recipient }
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage response;

        try
        {
            response = await _http.PostAsync("message", content, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // The gateway is a phone on somebody's desk. It goes flat, it leaves the network, it
            // gets picked up and rebooted — none of which should surface as an unhandled 500.
            _logger.LogError(ex, "The SMS gateway at {BaseUrl} could not be reached.", _options.BaseUrl);

            throw new DomainException("Could not send the verification code. Please try again shortly.");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // Log the gateway's reason but never the code, and give the caller a message that does
            // not leak which gateway sits behind us or what it said.
            _logger.LogError(
                "The SMS gateway rejected an OTP send to {Recipient}: {Status} {Body}",
                recipient, (int)response.StatusCode, body);

            throw new DomainException("Could not send the verification code. Please try again shortly.");
        }
    }

    /// <summary>
    /// The gateway dials what it is given, so a bare ten-digit number has to acquire a country
    /// code before it leaves here — a handset roaming outside India would otherwise dial a
    /// domestic number against the wrong network.
    /// </summary>
    private string ToE164(string phone)
    {
        if (phone.StartsWith('+'))
        {
            return phone;
        }

        return phone.Length == 10
            ? $"+{_options.DefaultCountryCode}{phone}"
            : $"+{phone}";
    }
}
