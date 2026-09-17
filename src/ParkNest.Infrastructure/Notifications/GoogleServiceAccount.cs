using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParkNest.Infrastructure.Notifications;

/// <summary>
/// The credentials Google issues for a Firebase project, and the OAuth dance FCM's v1 API needs.
///
/// Written by hand rather than pulling in Google's client libraries, which arrive with a
/// dependency tree far larger than the one call being made here. What is needed is narrow: sign a
/// JWT with the service account's private key, exchange it for an access token, and cache that
/// until it is nearly expired.
///
/// The legacy FCM endpoint took a static server key and would have been simpler. It was turned
/// off in 2024, and this is the interface that replaced it.
/// </summary>
public sealed class GoogleServiceAccount
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    /// <summary>
    /// How long before expiry a cached token is considered spent. A token that expires in flight
    /// costs a 401 and a retry; a minute of unused life costs nothing.
    /// </summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Credentials _credentials;

    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    private GoogleServiceAccount(Credentials credentials) => _credentials = credentials;

    public string ProjectId => _credentials.ProjectId;

    public static GoogleServiceAccount FromFile(string path)
    {
        var credentials = JsonSerializer.Deserialize<Credentials>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"'{path}' is not a Google service account key.");

        if (string.IsNullOrWhiteSpace(credentials.ClientEmail) || string.IsNullOrWhiteSpace(credentials.PrivateKey))
        {
            throw new InvalidOperationException(
                $"'{path}' is missing client_email or private_key. Download the key from Firebase console → Project settings → Service accounts.");
        }

        return new GoogleServiceAccount(credentials);
    }

    /// <summary>
    /// A valid bearer token for the messaging scope, minted only when the cached one is spent.
    ///
    /// The lock is not decoration: notifications fan out from several event handlers at once, and
    /// without it a burst of sends would each mint their own token.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(
        HttpClient http,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_accessToken is { } cached && now + Margin < _expiresAt)
        {
            return cached;
        }

        await _refreshLock.WaitAsync(cancellationToken);

        try
        {
            // Checked again inside the lock: whoever was ahead in the queue has already refreshed.
            if (_accessToken is { } current && now + Margin < _expiresAt)
            {
                return current;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = SignAssertion(now),
                }),
            };

            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Google refused the service account assertion: {(int)response.StatusCode} {body}");
            }

            var token = JsonSerializer.Deserialize<AccessTokenResponse>(body)
                ?? throw new InvalidOperationException("Google's token response could not be read.");

            _accessToken = token.AccessToken;
            _expiresAt = now.AddSeconds(token.ExpiresIn);

            return _accessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// A self-signed JWT saying who we are and what we want, which Google trades for an access
    /// token. An hour is the maximum Google accepts and the assertion is used immediately anyway.
    /// </summary>
    private string SignAssertion(DateTimeOffset now)
    {
        var issuedAt = now.ToUnixTimeSeconds();

        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = _credentials.ClientEmail,
            ["scope"] = Scope,
            ["aud"] = TokenEndpoint,
            ["iat"] = issuedAt,
            ["exp"] = issuedAt + 3600,
        }));

        var payload = $"{header}.{claims}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(_credentials.PrivateKey);

        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{payload}.{Base64Url(signature)}";
    }

    /// <summary>Base64url: JWT's alphabet, without the padding that '=' would add.</summary>
    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Credentials
    {
        [JsonPropertyName("project_id")]
        public string ProjectId { get; init; } = string.Empty;

        [JsonPropertyName("client_email")]
        public string ClientEmail { get; init; } = string.Empty;

        [JsonPropertyName("private_key")]
        public string PrivateKey { get; init; } = string.Empty;
    }

    private sealed record AccessTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; init; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }
}
