using Microsoft.Extensions.Options;
using ParkNest.Application.Options;

namespace ParkNest.Application.Payments;

/// <summary>
/// Every URL a payment hands to a provider or a browser, built in one place from
/// <see cref="PaymentOptions.PublicBaseUrl"/> — never from the incoming request's Host header,
/// which whoever sends the request controls.
///
/// With no public base URL (the sandbox, in development) the URLs are relative, which is right:
/// the sandbox's pages are served by this same API, so the browser resolves them against it.
/// </summary>
public sealed class PaymentUrls
{
    /// <summary>The admin site's wallet page. What an order returns to unless told otherwise.</summary>
    public const string DefaultReturnTo = "admin";

    /// <summary>
    /// The mobile app. There is no web page to go back to — the app is polling the order — so its
    /// return trip ends on a plain "go back to ParkNest" page instead of a redirect.
    /// </summary>
    public const string AppReturnTo = "app";

    private readonly PaymentOptions _options;

    public PaymentUrls(IOptions<PaymentOptions> options) => _options = options.Value;

    private string Base => _options.PublicBaseUrl.Trim().TrimEnd('/');

    /// <summary>Where the provider sends the browser after checkout, for one order.</summary>
    public string ReturnUrl(Guid orderId) => $"{Base}/checkout/return/{orderId}";

    /// <summary>The page that opens a JavaScript-only provider's payment sheet.</summary>
    public string CheckoutUrl(string providerOrderId) => $"{Base}/checkout/{Uri.EscapeDataString(providerOrderId)}";

    /// <summary>Where a provider calls back. Null without a public address — nothing could reach it.</summary>
    public string? WebhookUrl => string.IsNullOrWhiteSpace(Base) ? null : $"{Base}/api/payments/webhook";

    public bool IsKnownReturnTo(string? returnTo)
    {
        var key = Normalise(returnTo);
        return key == AppReturnTo || _options.ReturnUrls.ContainsKey(key);
    }

    /// <summary>
    /// The allow-listed client page an order returns to, with the order id appended so the page can
    /// poll it. Null for the app, and for anything not on the list — the caller then shows a page
    /// rather than redirecting, which is what keeps the return endpoint from being an open redirect.
    /// </summary>
    public string? ClientReturnUrl(string? returnTo, Guid orderId)
    {
        var key = Normalise(returnTo);

        if (key == AppReturnTo || !_options.ReturnUrls.TryGetValue(key, out var url))
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var separator = url.Contains('?') ? "&" : "?";
        return $"{url}{separator}orderId={orderId}";
    }

    public static string Normalise(string? returnTo) =>
        string.IsNullOrWhiteSpace(returnTo) ? DefaultReturnTo : returnTo.Trim().ToLowerInvariant();
}
