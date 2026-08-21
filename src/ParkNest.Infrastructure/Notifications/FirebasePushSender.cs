using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;

namespace ParkNest.Infrastructure.Notifications;

/// <summary>
/// Pushes through Firebase Cloud Messaging's HTTP v1 API.
///
/// One request per token, because v1 has no batch endpoint that survived the retirement of the
/// legacy one — the fan-out is per device and small (a person has one or two installs), and it
/// runs on a background task well after the request that caused it.
///
/// Nothing here throws at the caller. A push is best-effort by definition: the message has
/// already been stored, and a failure to buzz a phone must never surface as an error on a
/// checkout that has settled.
/// </summary>
public sealed class FirebasePushSender : IPushSender
{
    private readonly HttpClient _http;
    private readonly GoogleServiceAccount _account;
    private readonly IClock _clock;
    private readonly ILogger<FirebasePushSender> _logger;

    public FirebasePushSender(
        HttpClient http,
        GoogleServiceAccount account,
        IClock clock,
        ILogger<FirebasePushSender> logger)
    {
        _http = http;
        _account = account;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<string>> SendAsync(
        IReadOnlyCollection<string> tokens,
        PushMessage message,
        CancellationToken cancellationToken = default)
    {
        var dead = new List<string>();

        string accessToken;

        try
        {
            accessToken = await _account.GetAccessTokenAsync(_http, _clock.UtcNow, cancellationToken);
        }
        catch (Exception ex)
        {
            // Credentials that will not mint a token are a deployment fault, not a dead device.
            // Nothing is pruned, and the next notification tries again.
            _logger.LogError(ex, "Could not authenticate with Firebase; {Count} push(es) skipped.", tokens.Count);
            return dead;
        }

        var endpoint = $"https://fcm.googleapis.com/v1/projects/{_account.ProjectId}/messages:send";

        foreach (var token in tokens)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(Payload(token, message), Encoding.UTF8, "application/json"),
                };

                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                using var response = await _http.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                // 404 is FCM's "this token no longer exists" — the app was uninstalled, or the
                // token was rotated. 400 UNREGISTERED says the same about a malformed leftover.
                // Everything else (429, 5xx) is transient and the token stays registered.
                if (response.StatusCode == HttpStatusCode.NotFound
                    || body.Contains("UNREGISTERED", StringComparison.Ordinal))
                {
                    dead.Add(token);
                    continue;
                }

                _logger.LogWarning(
                    "Firebase refused a {Kind} push: {Status} {Body}",
                    message.Kind, (int)response.StatusCode, body);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A {Kind} push could not be delivered.", message.Kind);
            }
        }

        return dead;
    }

    /// <summary>
    /// The v1 message shape.
    ///
    /// Both a notification block and a data block. The notification is what the OS draws while the
    /// app is closed; the data is what the app reads to open the right booking when the user taps
    /// it — and every data value has to be a string, which is FCM's rule rather than ours.
    /// </summary>
    private static string Payload(string token, PushMessage message) => JsonSerializer.Serialize(new
    {
        message = new
        {
            token,
            notification = new { title = message.Title, body = message.Body },
            data = new Dictionary<string, string>
            {
                ["kind"] = message.Kind,
                ["subjectId"] = message.SubjectId?.ToString() ?? string.Empty,
            },
            android = new
            {
                // An overstay billing itself is worth waking the handset for; nothing else here
                // arrives often enough for high priority to be abuse.
                priority = "high",
            },
        },
    });
}
