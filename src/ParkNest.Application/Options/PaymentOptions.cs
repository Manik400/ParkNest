namespace ParkNest.Application.Options;

/// <summary>
/// Which gateway takes the money, how it reaches us, and the credentials each provider needs.
///
/// Each provider has its own section rather than sharing one KeyId/KeySecret pair: the names mean
/// different things at each provider, and a single flat set made "Provider=X with provider Y's keys"
/// a configuration that looked valid. <c>PaymentRegistration</c> validates all of this at startup.
/// See ADR 0007 and docs/payment-gateway-setup-fully-free-rnd.md for why these providers.
/// </summary>
public sealed class PaymentOptions
{
    public const string SectionName = "Payments";

    public const string NoneProvider = "None";
    public const string SandboxProvider = "Sandbox";
    public const string RazorpayProvider = "Razorpay";

    /// <summary>Providers this build has an adapter for.</summary>
    public static readonly IReadOnlyList<string> KnownProviders =
        new[] { NoneProvider, SandboxProvider, RazorpayProvider };

    /// <summary>"Sandbox" for the built-in local gateway, a provider name for real money, "None" to disable payments.</summary>
    public string Provider { get; set; } = NoneProvider;

    /// <summary>
    /// "Test" for a provider's test keys, "Live" for real money. Production refuses Test: test
    /// payments move no money, so crediting wallets from them is minting credits.
    /// </summary>
    public string Mode { get; set; } = "Test";

    /// <summary>
    /// The API's public address, e.g. <c>https://api.parknest.in</c>, or a tunnel URL in
    /// development. Providers need it to send the browser back and to call the webhook. Empty is
    /// allowed only for the sandbox, whose pages are served by this API itself.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Where each client lands after checkout, keyed by what it passed as <c>returnTo</c>. The
    /// order id is appended. The mobile app ("app") is not listed: it gets a "return to the app"
    /// page, since it is polling the order anyway.
    /// </summary>
    public Dictionary<string, string> ReturnUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["admin"] = "http://localhost:4200/wallet"
    };

    /// <summary>
    /// How long an unpaid order stays open before the sweep cancels it. Comfortably longer than a
    /// human takes to finish a checkout sheet, and longer again than a gateway takes to call back,
    /// because expiring an order that is about to be paid produces a confusing pair of records.
    /// </summary>
    public int OrderExpiryMinutes { get; set; } = 30;

    /// <summary>How often the sweep runs.</summary>
    public int ExpirySweepIntervalMinutes { get; set; } = 5;

    /// <summary>How old an order must be before a client's poll triggers a question to the gateway.</summary>
    public int ReconcileAfterSeconds { get; set; } = 15;

    /// <summary>The shortest gap between two questions to the gateway about the same order.</summary>
    public int ReconcileMinIntervalSeconds { get; set; } = 10;

    public SandboxGatewayOptions Sandbox { get; set; } = new();

    public RazorpayGatewayOptions Razorpay { get; set; } = new();

    public bool IsProvider(string name) =>
        string.Equals(Provider?.Trim(), name, StringComparison.OrdinalIgnoreCase);

    public bool IsSandbox => IsProvider(SandboxProvider);

    public bool IsDisabled => string.IsNullOrWhiteSpace(Provider) || IsProvider(NoneProvider);

    public bool IsKnownProvider => IsDisabled || KnownProviders.Any(IsProvider);

    public bool IsLive => string.Equals(Mode?.Trim(), "Live", StringComparison.OrdinalIgnoreCase);

    public bool IsKnownMode => IsLive || string.Equals(Mode?.Trim(), "Test", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a real gateway is selected and has everything it needs. The sandbox is excluded
    /// deliberately: it needs no credentials, and counting it here would let it stand in for a
    /// configured gateway in a check that exists to decide whether real money can move.
    /// </summary>
    public bool IsConfigured => IsProvider(RazorpayProvider) && Razorpay.IsConfigured;
}

public sealed class SandboxGatewayOptions
{
    /// <summary>
    /// Optional. Set, signatures survive a restart (handy for replaying a logged webhook); unset,
    /// each process signs with a random key, so nothing leaks into source control.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;
}

public sealed class RazorpayGatewayOptions
{
    /// <summary>Public key (<c>rzp_test_…</c> or <c>rzp_live_…</c>), safe to hand to the browser.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>API secret. Must come from a secret store, never source control.</summary>
    public string KeySecret { get; set; } = string.Empty;

    /// <summary>The secret set on the webhook in Razorpay's dashboard; it signs every callback body.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    public IReadOnlyList<string> MissingKeys =>
        new (string Name, string Value)[] { (nameof(KeyId), KeyId), (nameof(KeySecret), KeySecret), (nameof(WebhookSecret), WebhookSecret) }
            .Where(k => string.IsNullOrWhiteSpace(k.Value))
            .Select(k => k.Name)
            .ToList();

    public bool IsConfigured => MissingKeys.Count == 0;
}
