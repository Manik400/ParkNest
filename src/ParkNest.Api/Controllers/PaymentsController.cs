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
    private readonly IPaymentGateway _gateway;

    public PaymentsController(IPaymentService payments, IPaymentGateway gateway)
    {
        _payments = payments;
        _gateway = gateway;
    }

    /// <summary>
    /// Starts a credit purchase. Returns what the client SDK needs to open the checkout sheet.
    /// No credits are issued here — only a verified webhook does that.
    /// </summary>
    [HttpPost("orders")]
    public async Task<ActionResult<StartPaymentResult>> Start(
        [FromBody] StartPaymentRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _payments.StartAsync(request.Amount, cancellationToken));

    /// <summary>
    /// Where an order stands. The client polls this after checkout rather than believing the
    /// browser's return trip — the redirect proves the user came back, not that the money arrived.
    /// </summary>
    [HttpGet("orders/{orderId:guid}")]
    public async Task<ActionResult<PaymentOrderView>> Get(Guid orderId, CancellationToken cancellationToken) =>
        Ok(await _payments.GetOrderAsync(orderId, cancellationToken));

    /// <summary>
    /// Gateway callback. Anonymous by necessity — the gateway has no bearer token — so the
    /// signature is the only thing standing between this endpoint and free credits.
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

        var signature = Request.Headers[_gateway.SignatureHeader].ToString();

        var result = await _payments.HandleWebhookAsync(rawBody, signature, cancellationToken);

        // 200 even when the order is unknown: the signature was valid, so the gateway did its
        // part, and making it retry forever would not help.
        return Ok(new { accepted = result.Accepted, message = result.Message });
    }
}

public sealed record StartPaymentRequest(decimal Amount);
