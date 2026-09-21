using ParkNest.Application.Abstractions;
using ParkNest.Domain.Analytics;

namespace ParkNest.Application.Analytics;

/// <param name="Name">Must be one of <see cref="AnalyticsEventNames.ClientReportable"/>.</param>
/// <param name="Source">"web" or "app". Anything else is recorded as "web".</param>
public sealed record ClientAnalyticsHit(
    string Name,
    string? VisitorId,
    string? SessionId,
    string? Path,
    string? Referrer,
    string? Source,
    string? Detail);

public interface IAnalyticsCollector
{
    /// <summary>
    /// Takes a hit reported by a browser or the app. Returns false when it was dropped, which the
    /// endpoint answers to without complaint — telling a caller which names exist would only help
    /// somebody looking for the ones that do.
    /// </summary>
    Task<bool> CollectAsync(ClientAnalyticsHit hit, CancellationToken cancellationToken = default);
}

/// <summary>
/// The anonymous edge of the counter, and therefore the part that assumes the caller is hostile.
///
/// Everything a client sends is either matched against a fixed list or thrown away. A page view
/// carries a route path and nothing else: no query string, because that is where order ids and
/// return tokens live, and a referrer reduced to its origin, because the path of the page someone
/// arrived from is their business.
/// </summary>
public sealed class AnalyticsCollector : IAnalyticsCollector
{
    private readonly IAnalyticsRecorder _recorder;
    private readonly ICurrentUser _currentUser;

    public AnalyticsCollector(IAnalyticsRecorder recorder, ICurrentUser currentUser)
    {
        _recorder = recorder;
        _currentUser = currentUser;
    }

    public async Task<bool> CollectAsync(ClientAnalyticsHit hit, CancellationToken cancellationToken = default)
    {
        if (hit.Name is null || !AnalyticsEventNames.ClientReportable.Contains(hit.Name))
        {
            return false;
        }

        await _recorder.RecordAsync(
            new AnalyticsHit(
                hit.Name,
                // From the bearer token when one was sent, never from the body. The endpoint is
                // anonymous, so a user id a caller supplied would be a claim about somebody else.
                UserId: _currentUser.UserId,
                VisitorId: Opaque(hit.VisitorId),
                SessionId: Opaque(hit.SessionId),
                Path: RoutePath(hit.Path),
                Referrer: ReferrerOrigin(hit.Referrer),
                Source: hit.Source == AnalyticsSources.App ? AnalyticsSources.App : AnalyticsSources.Web,
                Detail: Opaque(hit.Detail)),
            cancellationToken);

        return true;
    }

    /// <summary>
    /// Ids the client minted for itself. Kept only if they look like what we hand out — letters,
    /// digits and dashes — so the column cannot be used to smuggle anything through.
    /// </summary>
    private static string? Opaque(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.Length > 64)
        {
            return null;
        }

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            {
                return null;
            }
        }

        return trimmed;
    }

    /// <summary>
    /// The route, with the query string and fragment cut off and ids in the path replaced by a
    /// placeholder. Without the second part, "top pages" would be a list of every booking anybody
    /// opened rather than the handful of screens the site actually has.
    /// </summary>
    private static string? RoutePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var path = value.Trim();
        var cut = path.IndexOfAny(new[] { '?', '#' });

        if (cut >= 0)
        {
            path = path[..cut];
        }

        if (!path.StartsWith('/'))
        {
            path = '/' + path;
        }

        if (path.Length > 256)
        {
            path = path[..256];
        }

        var segments = path.Split('/');

        for (var i = 0; i < segments.Length; i++)
        {
            if (LooksLikeAnId(segments[i]))
            {
                segments[i] = ":id";
            }
        }

        return string.Join('/', segments);
    }

    /// <summary>A GUID, or a long run of digits. Both are ids rather than screens.</summary>
    private static bool LooksLikeAnId(string segment) =>
        Guid.TryParse(segment, out _) ||
        (segment.Length >= 6 && segment.All(char.IsAsciiDigit));

    /// <summary>
    /// Where someone arrived from, as a scheme and host. The path is dropped: what matters is that
    /// they came from a search engine or a shared link, not which page of it.
    /// </summary>
    private static string? ReferrerOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var origin = $"{uri.Scheme}://{uri.Host}";
        return origin.Length > 256 ? origin[..256] : origin;
    }
}
