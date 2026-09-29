namespace ParkNest.Domain.Analytics;

/// <summary>
/// One thing that happened on the site, counted.
///
/// Deliberately not the ledger and not the booking table: this is a tally kept for the operator's
/// benefit, and nothing in the product may read it back or make a decision from it. That is what
/// lets it be lossy — a recorder that fails swallows the failure rather than breaking a checkout,
/// and a row missing from here costs a number on a dashboard, never a booking or a rupee.
///
/// It also carries no personal data. There is no IP address, no email, no name: a visit is
/// attributed to a random id the browser mints for itself, which the visitor can throw away by
/// clearing their storage. Counting people is not worth building a profile of them.
/// </summary>
public class AnalyticsEvent
{
    /// <summary>
    /// A sequence rather than a GUID. Every page view writes one of these, and a random key on
    /// the highest-volume table in the system would fragment the index for no benefit — nothing
    /// ever refers to a row here by id.
    /// </summary>
    public long Id { get; set; }

    /// <summary>One of <see cref="AnalyticsEventNames"/>. Anything else is refused at the edge.</summary>
    public string Name { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The signed-in user, when there was one. Null for a visitor who never signed in.</summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// The browser, as it identifies itself. A random id kept in local storage — not a fingerprint
    /// and not derived from anything about the person — so "unique visitors" means "browsers that
    /// did not clear their storage", which is the honest version of that number anyway.
    /// </summary>
    public string? VisitorId { get; set; }

    /// <summary>One sitting. Resets when the tab is closed, which is what separates visits.</summary>
    public string? SessionId { get; set; }

    /// <summary>Route path only — never the query string, which is where ids and tokens live.</summary>
    public string? Path { get; set; }

    /// <summary>Origin of the referring site, without its path. Null when the visit was direct.</summary>
    public string? Referrer { get; set; }

    /// <summary>Which surface reported it: "web", "app", or "server".</summary>
    public string Source { get; set; } = AnalyticsSources.Server;

    /// <summary>Credits involved, for the events that move money. Null for the rest.</summary>
    public decimal? Amount { get; set; }

    /// <summary>
    /// One short word of context — a payment provider, a failure reason, a sign-in channel.
    /// Free text, capped, and never rendered as anything but text.
    /// </summary>
    public string? Detail { get; set; }
}

public static class AnalyticsSources
{
    public const string Web = "web";
    public const string App = "app";
    public const string Server = "server";
}

/// <summary>
/// The whole vocabulary, in one place.
///
/// A closed set on purpose. The collect endpoint is anonymous, so an open one would let a stranger
/// mint a million distinct names and turn the dashboard into noise — and worse, make the "top
/// events" query unbounded. A name not listed here is dropped.
/// </summary>
public static class AnalyticsEventNames
{
    // --- Reported by a browser or the app -------------------------------------------------

    /// <summary>Someone opened the site: the first page of a new session. The hit counter.</summary>
    public const string SiteVisit = "site.visit";

    /// <summary>Every screen after that, including the first.</summary>
    public const string PageView = "page.view";

    /// <summary>A renter asked what a space would cost — the step before a booking.</summary>
    public const string SearchRun = "search.run";

    /// <summary>What a client is allowed to report. Everything else it sends is dropped.</summary>
    public static readonly IReadOnlySet<string> ClientReportable =
        new HashSet<string>(StringComparer.Ordinal)
        {
            SiteVisit,
            PageView,
            SearchRun
        };

    // --- Recorded by the API itself -------------------------------------------------------

    public const string SignInRequested = "auth.code_requested";
    public const string SignedIn = "auth.signed_in";
    public const string SignedUp = "auth.signed_up";

    public const string BookingCreated = "booking.created";
    public const string BookingCancelled = "booking.cancelled";
    public const string BookingCompleted = "booking.completed";

    /// <summary>An order was created with the gateway. The denominator of the payment funnel.</summary>
    public const string PaymentStarted = "payment.started";
    public const string PaymentSucceeded = "payment.succeeded";
    public const string PaymentFailed = "payment.failed";

    public const string DisputeRaised = "dispute.raised";

    // Adding another is one RecordAsync call and a constant here: the dashboard lists every name
    // that fired, so a new counter needs no change to the query or the screen.
}
