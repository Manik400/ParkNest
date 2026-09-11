using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ParkNest.Application.Payments;

namespace ParkNest.Api.Controllers;

[ApiController]
[Route("api/payments")]
public sealed class PaymentsController : ControllerBase
{
    private readonly IPaymentService _payments;

    public PaymentsController(IPaymentService payments)
    {
        _payments = payments;
    }

    /// <summary>
    /// Starts a credit purchase. Returns a payload whose <c>checkout_url</c> the client opens.
    /// No credits are issued here — only the gateway's confirmation does that.
    /// </summary>
    [HttpPost("orders")]
    public async Task<ActionResult<StartPaymentResult>> Start(
        [FromBody] StartPaymentRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _payments.StartAsync(request.Amount, request.ReturnTo, cancellationToken));

    /// <summary>
    /// Where an order stands. The client polls this after checkout rather than believing the
    /// browser's return trip — the redirect proves the user came back, not that the money arrived.
    /// </summary>
    [HttpGet("orders/{orderId:guid}")]
    public async Task<ActionResult<PaymentOrderView>> Get(Guid orderId, CancellationToken cancellationToken) =>
        Ok(await _payments.GetOrderAsync(orderId, cancellationToken));

    /// <summary>
    /// Gateway callback. Anonymous by necessity — the gateway has no bearer token — so its
    /// verification is the only thing standing between this endpoint and free credits.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.PaymentWebhook)]
    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook(CancellationToken cancellationToken)
    {
        // The raw body, byte for byte, is what was signed. Letting the JSON serialiser round-trip
        // it first would change the whitespace and break every signature check.
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync(cancellationToken);
        Request.Body.Position = 0;

        // Every header, because providers disagree about where the proof lives — a signature
        // header, a signature plus a timestamp, or Authorization. The gateway picks what it needs.
        var headers = Request.Headers.ToDictionary(
            h => h.Key,
            h => h.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);

        var result = await _payments.HandleWebhookAsync(new WebhookRequest(rawBody, headers), cancellationToken);

        // 200 even when the order is unknown: the webhook was verified, so the gateway did its
        // part, and making it retry forever would not help.
        return Ok(new { accepted = result.Accepted, message = result.Message });
    }
}

/// <param name="ReturnTo">
/// The client starting the payment: "admin" (the default) or "app". Decides where the browser
/// lands after checkout.
/// </param>
public sealed record StartPaymentRequest(decimal Amount, string? ReturnTo = null);
