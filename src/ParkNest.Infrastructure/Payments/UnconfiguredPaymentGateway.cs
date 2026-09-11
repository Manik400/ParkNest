using ParkNest.Application.Payments;
using ParkNest.Domain.Common;

namespace ParkNest.Infrastructure.Payments;

/// <summary>
/// Stands in when no gateway is configured, so the application still starts and every other
/// feature stays usable without merchant credentials.
///
/// Registering nothing instead would fail dependency-injection validation at startup and take the
/// whole API down. Failing here, per call, keeps the blast radius to the payment endpoints —
/// and it refuses every webhook, so an unconfigured deployment cannot be tricked into issuing
/// credits.
/// </summary>
public sealed class UnconfiguredPaymentGateway : IPaymentGateway
{
    private const string Message =
        "Payments are not configured on this server. Set the Payments section to enable them.";

    public string Name => "None";

    public bool WebhookIsAuthoritative => true;

    public Task<GatewayOrder> CreateOrderAsync(GatewayOrderRequest request, CancellationToken cancellationToken = default) =>
        throw new DomainException(Message);

    /// <summary>Always false. Without a shared secret nothing can be verified, so nothing is trusted.</summary>
    public bool VerifyWebhook(WebhookRequest request) => false;

    public GatewayOutcome ParseWebhook(string rawBody) => throw new DomainException(Message);

    /// <summary>There is no gateway to ask.</summary>
    public Task<GatewayOutcome?> QueryOrderAsync(string providerOrderId, CancellationToken cancellationToken = default) =>
        Task.FromResult<GatewayOutcome?>(null);
}
