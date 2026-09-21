namespace ParkNest.Api;

/// <summary>
/// Names of the rate-limiting policies registered in <c>Program</c>. Constants rather than literals
/// because a typo in an <c>[EnableRateLimiting]</c> attribute throws at request time, on the exact
/// endpoint the policy was meant to protect.
/// </summary>
public static class RateLimitPolicies
{
    public const string OtpRequest = "otp-request";
    public const string OtpVerify = "otp-verify";
    public const string PaymentWebhook = "payment-webhook";
    public const string CheckoutPage = "checkout-page";
    public const string AnalyticsCollect = "analytics-collect";
}

/// <summary>
/// Per-caller ceilings for the anonymous endpoints. Production values by default; Development
/// raises them so an end-to-end run, which signs in an account per test, is not throttled.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    public int OtpRequestsPer15Minutes { get; set; } = 10;
    public int OtpVerifiesPer15Minutes { get; set; } = 30;
}
