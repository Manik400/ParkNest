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
}
