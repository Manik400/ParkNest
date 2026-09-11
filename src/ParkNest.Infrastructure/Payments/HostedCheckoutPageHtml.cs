using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using ParkNest.Domain.Payments;

namespace ParkNest.Infrastructure.Payments;

/// <summary>
/// The page a JavaScript-only provider's checkout is opened from (see <see cref="IHostedCheckoutPage"/>).
/// One shell for every such provider, so the only provider-specific part is the few lines that
/// start its SDK.
/// </summary>
internal static class HostedCheckoutPageHtml
{
    /// <param name="begin">
    /// JavaScript defining <c>function begin()</c>, which opens the provider's sheet. The constant
    /// <c>RETURN_URL</c> is in scope. Anything interpolated into it must already be JSON-encoded.
    /// </param>
    public static string Render(string providerName, PaymentOrder order, string returnUrl, string sdkUrl, string begin)
    {
        var html = HtmlEncoder.Default;
        var amount = $"{order.Currency} {order.Amount.ToString("N2", CultureInfo.InvariantCulture)}";

        // JsonSerializer escapes <, > and & by default, which is what makes a serialised string
        // safe to drop into a <script> block.
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>ParkNest checkout</title>
              <style>
                :root { color-scheme: light dark; }
                * { box-sizing: border-box; }
                body {
                  margin: 0; min-height: 100vh; display: grid; place-items: center; padding: 24px;
                  font: 16px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif;
                  background: #f4f5f7; color: #16181d;
                }
                @media (prefers-color-scheme: dark) {
                  body { background: #101216; color: #e8eaed; }
                  .sheet { background: #191c22; border-color: #2a2f39; }
                  .muted { color: #9aa1ad; }
                }
                .sheet {
                  width: 100%; max-width: 420px; background: #fff; border: 1px solid #e2e5ea;
                  border-radius: 14px; padding: 28px; text-align: center;
                }
                h1 { margin: 0 0 4px; font-size: 1.1rem; }
                .amount { font-size: 2rem; font-weight: 700; margin: 14px 0 6px; font-variant-numeric: tabular-nums; }
                .muted { color: #63697a; font-size: .88rem; margin: 0 0 18px; }
                button {
                  font: inherit; font-weight: 600; width: 100%; padding: 12px; border-radius: 9px;
                  border: 0; background: #10893e; color: #fff; cursor: pointer;
                }
                a { display: inline-block; margin-top: 14px; color: inherit; font-size: .88rem; }
              </style>
            </head>
            <body>
              <main class="sheet">
                <h1>ParkNest — parking credits</h1>
                <div class="amount">{{html.Encode(amount)}}</div>
                <p class="muted" id="status">Opening the {{html.Encode(providerName)}} payment page…</p>
                <button id="pay" type="button">Pay {{html.Encode(amount)}}</button>
                <a href="{{html.Encode(returnUrl)}}">Cancel and go back</a>
              </main>

              <script src="{{html.Encode(sdkUrl)}}"></script>
              <script>
                const RETURN_URL = {{JsonSerializer.Serialize(returnUrl)}};
                const status = document.getElementById('status');

                {{begin}}

                function start() {
                  try {
                    begin();
                  } catch (err) {
                    status.textContent = 'The payment page could not be opened. Check your connection and try again.';
                  }
                }

                document.getElementById('pay').addEventListener('click', start);
                window.addEventListener('load', start);
              </script>
            </body>
            </html>
            """;
    }
}
