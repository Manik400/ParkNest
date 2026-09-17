using ParkNest.Domain.Payments;

namespace ParkNest.Infrastructure.Payments;

/// <summary>
/// A gateway whose payment sheet can only be opened by its JavaScript SDK, not by a URL.
///
/// Such gateways get a small page served by this API (<c>/checkout/{providerOrderId}</c>) that
/// loads the SDK and opens the sheet, and their checkout payload points there. Neither the admin
/// site nor the mobile app ever needs a provider's SDK: both just open <c>checkout_url</c>.
/// </summary>
public interface IHostedCheckoutPage
{
    /// <param name="returnUrl">Where the provider must send the browser afterwards; absolute for real providers.</param>
    Task<string> RenderAsync(PaymentOrder order, string returnUrl, CancellationToken cancellationToken = default);
}
