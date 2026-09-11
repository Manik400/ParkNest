using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Payments;
using ParkNest.Domain.Payments;
using ParkNest.Infrastructure.Payments;

namespace ParkNest.Api.Controllers;

/// <summary>
/// The two browser-facing ends of a real checkout.
///
/// <c>/checkout/{providerOrderId}</c> is a small page that opens a provider's payment sheet, for
/// providers that only offer a JavaScript SDK. Serving it here means neither the admin site nor
/// the mobile app needs any provider's SDK: both just open a URL.
///
/// <c>/checkout/return/{orderId}</c> is where every provider sends the browser afterwards. It asks
/// the gateway where the payment stands — so credits land even when no webhook can reach this
/// machine — then sends the user on to the client that started it. That destination comes from an
/// allow-list keyed by what the order recorded at creation, never from the request, so the endpoint
/// cannot be turned into an open redirect.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("checkout")]
[ApiExplorerSettings(IgnoreApi = true)]
[EnableRateLimiting(RateLimitPolicies.CheckoutPage)]
public sealed class CheckoutController : ControllerBase
{
    private readonly IPaymentGateway _gateway;
    private readonly IParkNestDbContext _db;
    private readonly IPaymentReconciler _reconciler;
    private readonly PaymentUrls _urls;

    public CheckoutController(
        IPaymentGateway gateway,
        IParkNestDbContext db,
        IPaymentReconciler reconciler,
        PaymentUrls urls)
    {
        _gateway = gateway;
        _db = db;
        _reconciler = reconciler;
        _urls = urls;
    }

    /// <summary>Opens the provider's payment sheet for an order.</summary>
    [HttpGet("{providerOrderId}")]
    public async Task<IActionResult> Checkout(string providerOrderId, CancellationToken cancellationToken)
    {
        // The origin only, not the path: some providers check which site opened their sheet.
        HardenResponse(referrerPolicy: "strict-origin");

        if (_gateway is not IHostedCheckoutPage page)
        {
            return NotFound();
        }

        var order = await _db.PaymentOrders
            .FirstOrDefaultAsync(o => o.ProviderOrderId == providerOrderId, cancellationToken);

        if (order is null)
        {
            return NotFound();
        }

        var returnUrl = _urls.ReturnUrl(order.Id);

        // Nothing left to pay on a settled order: go straight to where it ends.
        if (order.Status != PaymentOrderStatus.Created)
        {
            return Redirect(returnUrl);
        }

        var html = await page.RenderAsync(order, returnUrl, cancellationToken);

        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>
    /// Where the provider sends the browser after checkout. POST as well as GET because some
    /// providers post their result here; whatever they post is ignored.
    /// </summary>
    [HttpGet("return/{orderId:guid}")]
    [HttpPost("return/{orderId:guid}")]
    public async Task<IActionResult> Return(Guid orderId, CancellationToken cancellationToken)
    {
        HardenResponse(referrerPolicy: "no-referrer");

        var order = await _db.PaymentOrders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

        if (order is null)
        {
            return NotFound();
        }

        // The answer comes from asking the gateway ourselves, not from anything in this request.
        // Throttled by the order's LastCheckedAt, and it never throws for a provider hiccup.
        await _reconciler.ReconcileAsync(order.Id, "return", cancellationToken);

        var target = _urls.ClientReturnUrl(order.ReturnTo, order.Id);

        return target is not null
            ? Redirect(target)
            : Content(RenderAppReturnPage(order), "text/html; charset=utf-8");
    }

    private void HardenResponse(string referrerPolicy)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = referrerPolicy;
        Response.Headers["X-Frame-Options"] = "DENY";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    /// <summary>
    /// The end of the road for the mobile app's browser: the app is polling the order and will
    /// update by itself, so all this page has to do is say so.
    /// </summary>
    private static string RenderAppReturnPage(PaymentOrder order)
    {
        var html = HtmlEncoder.Default;
        var amount = $"{order.Currency} {order.Amount.ToString("N2", CultureInfo.InvariantCulture)}";

        var (heading, detail) = order.Status switch
        {
            PaymentOrderStatus.Paid => ("Payment received", $"{amount} in credits has been added to your wallet."),
            PaymentOrderStatus.Failed => ("Payment did not go through", "No money was taken for this attempt. You can try again from the app."),
            PaymentOrderStatus.Cancelled => ("Payment not completed", "This payment has expired. You can start a new one from the app."),
            _ => ("Payment submitted", "Your credits will appear in the app as soon as the payment is confirmed.")
        };

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>ParkNest</title>
              <style>
                :root { color-scheme: light dark; }
                body {
                  margin: 0; min-height: 100vh; display: grid; place-items: center; padding: 24px;
                  font: 16px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif;
                  background: #f4f5f7; color: #16181d; text-align: center;
                }
                @media (prefers-color-scheme: dark) { body { background: #101216; color: #e8eaed; } }
                main { max-width: 380px; }
                h1 { font-size: 1.3rem; margin: 0 0 8px; }
                p { margin: 0 0 8px; }
                .muted { opacity: .7; font-size: .9rem; }
              </style>
            </head>
            <body>
              <main>
                <h1>{{html.Encode(heading)}}</h1>
                <p>{{html.Encode(detail)}}</p>
                <p class="muted">You can close this page and return to the ParkNest app.</p>
              </main>
            </body>
            </html>
            """;
    }
}
