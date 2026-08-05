using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Payments;
using ParkNest.Infrastructure.Payments;

namespace ParkNest.Api.Controllers;

/// <summary>
/// The hosted checkout page a real payment provider would serve on its own domain, served here
/// instead so the recharge flow can be driven end to end without a merchant account.
///
/// It exists only while <c>Payments:Provider=Sandbox</c> is selected: every action first checks
/// that the registered gateway really is the sandbox and 404s otherwise. Since startup refuses the
/// sandbox provider in Production, there is no configuration in which these routes are reachable
/// from a deployment that handles real money.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("sandbox/checkout")]
public sealed class SandboxCheckoutController : ControllerBase
{
    private readonly IPaymentGateway _gateway;
    private readonly IParkNestDbContext _db;

    public SandboxCheckoutController(IPaymentGateway gateway, IParkNestDbContext db)
    {
        _gateway = gateway;
        _db = db;
    }

    /// <summary>Renders the fake payment sheet for an order.</summary>
    [HttpGet("{providerOrderId}")]
    public async Task<IActionResult> Checkout(string providerOrderId, CancellationToken cancellationToken)
    {
        if (_gateway is not SandboxPaymentGateway sandbox)
        {
            return NotFound();
        }

        var order = await _db.PaymentOrders
            .FirstOrDefaultAsync(o => o.ProviderOrderId == providerOrderId, cancellationToken);

        if (order is null)
        {
            return NotFound();
        }

        // Both outcomes are signed up front so the page can post either without a round trip. In a
        // real integration this signing happens on the provider's servers; the sandbox is the
        // provider, so it happens here.
        var captured = sandbox.BuildWebhook(providerOrderId, succeeded: true);
        var failed = sandbox.BuildWebhook(providerOrderId, succeeded: false);

        var html = RenderCheckoutPage(
            providerOrderId,
            order.Id,
            order.Amount,
            order.Currency,
            alreadySettled: order.IsSettled,
            captured,
            failed,
            sandbox.ReturnUrl);

        return Content(html, "text/html; charset=utf-8");
    }

    private static string RenderCheckoutPage(
        string providerOrderId,
        Guid orderId,
        decimal amount,
        string currency,
        bool alreadySettled,
        SignedWebhook captured,
        SignedWebhook failed,
        string returnUrl)
    {
        // JsonSerializer.Serialize on a string yields a quoted, escaped JavaScript string literal,
        // which is what makes embedding the signed bodies safe. HtmlEncoder covers the visible text.
        static string Js(string value) => JsonSerializer.Serialize(value);
        var html = HtmlEncoder.Default;

        var settledNotice = alreadySettled
            ? """<p class="notice">This order has already been settled. Paying again will be acknowledged and ignored — replays cannot credit twice.</p>"""
            : string.Empty;

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>ParkNest Sandbox Checkout</title>
              <style>
                :root { color-scheme: light dark; }
                * { box-sizing: border-box; }
                body {
                  margin: 0; min-height: 100vh; display: grid; place-items: center;
                  font: 16px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif;
                  background: #f4f5f7; color: #16181d; padding: 24px;
                }
                @media (prefers-color-scheme: dark) {
                  body { background: #101216; color: #e8eaed; }
                  .sheet { background: #191c22; border-color: #2a2f39; }
                  .row { border-color: #2a2f39; }
                  .muted { color: #9aa1ad; }
                }
                .sheet {
                  width: 100%; max-width: 420px; background: #fff; border: 1px solid #e2e5ea;
                  border-radius: 14px; padding: 28px; box-shadow: 0 8px 30px rgb(0 0 0 / 8%);
                }
                h1 { margin: 0 0 4px; font-size: 1.15rem; }
                .badge {
                  display: inline-block; font-size: .7rem; letter-spacing: .06em; text-transform: uppercase;
                  background: #ffb020; color: #3d2b00; padding: 3px 8px; border-radius: 999px; font-weight: 700;
                }
                .amount { font-size: 2.1rem; font-weight: 700; margin: 18px 0 4px; font-variant-numeric: tabular-nums; }
                .muted { color: #63697a; font-size: .85rem; margin: 0; }
                .row {
                  display: flex; justify-content: space-between; gap: 12px;
                  border-top: 1px solid #e2e5ea; padding: 10px 0; font-size: .85rem;
                }
                .row code { font-size: .8rem; word-break: break-all; }
                .actions { display: grid; gap: 10px; margin-top: 22px; }
                button {
                  font: inherit; font-weight: 600; padding: 12px; border-radius: 9px;
                  border: 1px solid transparent; cursor: pointer;
                }
                button[disabled] { opacity: .55; cursor: progress; }
                .pay { background: #10893e; color: #fff; }
                .fail { background: transparent; color: #c0392b; border-color: currentColor; }
                .notice {
                  background: #fff4d6; color: #5c4400; border-radius: 8px; padding: 10px 12px;
                  font-size: .82rem; margin: 16px 0 0;
                }
                .error { background: #fde8e6; color: #8c1d13; }
                .explain { margin-top: 20px; font-size: .78rem; }
              </style>
            </head>
            <body>
              <main class="sheet">
                <span class="badge">Sandbox</span>
                <h1>ParkNest — parking credits</h1>
                <p class="muted">No money moves. This page stands in for a real payment provider.</p>

                <div class="amount">{{html.Encode(currency)}} {{html.Encode(amount.ToString("N2"))}}</div>
                <p class="muted">Credits are issued only when the signed callback is verified.</p>

                <div class="row"><span class="muted">Gateway order</span><code>{{html.Encode(providerOrderId)}}</code></div>
                <div class="row"><span class="muted">ParkNest order</span><code>{{html.Encode(orderId.ToString())}}</code></div>

                {{settledNotice}}

                <div class="actions">
                  <button class="pay" id="pay" type="button">Pay {{html.Encode(currency)}} {{html.Encode(amount.ToString("N2"))}}</button>
                  <button class="fail" id="fail" type="button">Simulate a declined payment</button>
                </div>

                <p id="status" class="notice" hidden></p>

                <p class="muted explain">
                  Either button posts an HMAC-SHA256-signed callback to
                  <code>/api/payments/webhook</code>, exactly as a provider would. The signature is
                  what the server trusts — not this page, and not the redirect that follows.
                </p>
              </main>

              <script>
                const CAPTURED = { body: {{Js(captured.Body)}}, signature: {{Js(captured.Signature)}} };
                const FAILED   = { body: {{Js(failed.Body)}},   signature: {{Js(failed.Signature)}} };
                const HEADER   = {{Js(captured.HeaderName)}};
                const RETURN   = {{Js(returnUrl)}};
                const ORDER_ID = {{Js(orderId.ToString())}};

                const status = document.getElementById('status');

                function show(message, isError) {
                  status.textContent = message;
                  status.classList.toggle('error', !!isError);
                  status.hidden = false;
                }

                async function send(outcome) {
                  document.querySelectorAll('button').forEach(b => b.disabled = true);
                  show('Sending the signed callback…', false);

                  try {
                    // The body is transmitted byte for byte as it was signed. Anything that
                    // re-encodes it here would break verification on the server.
                    const response = await fetch('/api/payments/webhook', {
                      method: 'POST',
                      headers: { 'Content-Type': 'application/json', [HEADER]: outcome.signature },
                      body: outcome.body,
                    });

                    if (!response.ok) {
                      throw new Error('The server rejected the callback (' + response.status + ').');
                    }

                    const result = await response.json();
                    show('Accepted: ' + result.message + ' Returning…', false);

                    const separator = RETURN.includes('?') ? '&' : '?';
                    setTimeout(() => { window.location.href = RETURN + separator + 'orderId=' + ORDER_ID; }, 900);
                  } catch (err) {
                    show(err.message, true);
                    document.querySelectorAll('button').forEach(b => b.disabled = false);
                  }
                }

                document.getElementById('pay').addEventListener('click', () => send(CAPTURED));
                document.getElementById('fail').addEventListener('click', () => send(FAILED));
              </script>
            </body>
            </html>
            """;
    }
}
