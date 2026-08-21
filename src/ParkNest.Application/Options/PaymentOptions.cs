namespace ParkNest.Application.Options;

/// <summary>
/// Which gateway takes the money, and the credentials it needs.
///
/// Three providers, and the difference between them is only ever the edge of the system — the
/// order record, the ledger and the webhook verification path are identical whichever is selected.
/// </summary>
public sealed class PaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary>
    /// "Razorpay" for real money, "Sandbox" for the built-in local gateway, "None" to disable
    /// payments entirely.
    /// </summary>
    public string Provider { get; set; } = "None";

    /// <summary>Public key, safe to hand to the client SDK.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>Secret for signing API calls. Must come from a secret store, never source control.</summary>
    public string KeySecret { get; set; } = string.Empty;

    /// <summary>Shared secret the gateway signs webhook bodies with.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Where the browser is sent after the sandbox checkout finishes. The admin console's wallet
    /// page, which then polls the order rather than trusting the redirect.
    /// </summary>
    public string SandboxReturnUrl { get; set; } = "http://localhost:4200/wallet";

    public bool IsSandbox => string.Equals(Provider, "Sandbox", StringComparison.OrdinalIgnoreCase);

    public bool IsDisabled =>
        string.IsNullOrWhiteSpace(Provider) || string.Equals(Provider, "None", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a real gateway has everything it needs. The sandbox is excluded deliberately: it
    /// needs no credentials, and folding it in here would let it stand in for a configured gateway
    /// in a check that exists to decide whether real money can move.
    /// </summary>
    public bool IsConfigured =>
        !IsDisabled
        && !IsSandbox
        && !string.IsNullOrWhiteSpace(KeyId)
        && !string.IsNullOrWhiteSpace(KeySecret)
        && !string.IsNullOrWhiteSpace(WebhookSecret);
}
