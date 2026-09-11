using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ParkNest.Application.Options;
using ParkNest.Application.Payments;

namespace ParkNest.Infrastructure.Payments;

/// <summary>
/// Picks the payment gateway from <c>Payments:Provider</c> — actually from it; the previous wiring
/// registered Razorpay whenever keys were present, whatever the setting said.
///
/// Everything that could make a deployment move money wrongly fails here, at startup, with a
/// message naming the key to fix: an unknown provider, a half-filled section, test keys or the
/// sandbox in Production. A payment configuration that is wrong should never get as far as the
/// first user's recharge.
/// </summary>
public static class PaymentRegistration
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Flat keys from before each provider got its own section.</summary>
    private static readonly string[] RetiredKeys = { "KeyId", "KeySecret", "WebhookSecret", "SandboxReturnUrl" };

    public static IServiceCollection AddPaymentGateway(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var section = configuration.GetSection(PaymentOptions.SectionName);
        var payments = section.Get<PaymentOptions>() ?? new PaymentOptions();

        Validate(section, payments, environment);

        if (payments.IsProvider(PaymentOptions.RazorpayProvider))
        {
            services.AddHttpClient<IPaymentGateway, RazorpayPaymentGateway>(http => http.Timeout = HttpTimeout);
            return services;
        }

        if (payments.IsSandbox)
        {
            // Singleton, and it has to be: when no WebhookSecret is configured the sandbox
            // generates a per-instance signing key, so a scoped registration would sign the
            // checkout page with one key and verify the callback with another.
            services.AddSingleton<SandboxPaymentGateway>();
            services.AddSingleton<IPaymentGateway>(sp => sp.GetRequiredService<SandboxPaymentGateway>());
            return services;
        }

        // A stand-in rather than nothing: leaving IPaymentGateway unregistered fails DI validation
        // at startup and takes the entire API down, which is a far worse development experience
        // than the payment endpoints returning a clear "not configured".
        services.AddScoped<IPaymentGateway, UnconfiguredPaymentGateway>();
        return services;
    }

    private static void Validate(IConfigurationSection section, PaymentOptions payments, IHostEnvironment environment)
    {
        var retired = RetiredKeys.Where(key => !string.IsNullOrWhiteSpace(section[key])).ToList();

        if (retired.Count > 0)
        {
            // Silently ignoring a key someone set is how a deployment ends up on the sandbox while
            // its operator believes it is live.
            throw new InvalidOperationException(
                $"{string.Join(", ", retired.Select(k => $"Payments:{k}"))} moved into per-provider sections " +
                "(Payments:Razorpay:KeyId, Payments:Sandbox:WebhookSecret, Payments:ReturnUrls:admin, …). Move the value and remove the old key.");
        }

        if (!payments.IsKnownProvider)
        {
            throw new InvalidOperationException(
                $"Payments:Provider '{payments.Provider}' is not supported. Choose one of: {string.Join(", ", PaymentOptions.KnownProviders)}. " +
                "See docs/payment-gateway-setup-fully-free-rnd.md for why these.");
        }

        if (!payments.IsKnownMode)
        {
            throw new InvalidOperationException($"Payments:Mode '{payments.Mode}' must be Test or Live.");
        }

        if (environment.IsProduction())
        {
            if (payments.IsSandbox)
            {
                throw new InvalidOperationException(
                    "Payments:Provider=Sandbox is a development gateway that issues credits without taking money. Configure a real provider before deploying to Production.");
            }

            if (payments.IsDisabled)
            {
                throw new InvalidOperationException(
                    "No payment gateway is configured. Set Payments:Provider and that provider's section before deploying to Production.");
            }

            if (!payments.IsLive)
            {
                throw new InvalidOperationException(
                    "Payments:Mode=Test in Production credits wallets from test payments that move no money — the same as minting credits. Set Mode=Live with live keys.");
            }

            if (!IsAbsoluteHttp(payments.PublicBaseUrl, requireHttps: true))
            {
                throw new InvalidOperationException(
                    "Payments:PublicBaseUrl must be the API's public https address in Production; providers redirect to it and call its webhook.");
            }
        }

        if (!string.IsNullOrWhiteSpace(payments.PublicBaseUrl) && !IsAbsoluteHttp(payments.PublicBaseUrl, requireHttps: false))
        {
            throw new InvalidOperationException(
                $"Payments:PublicBaseUrl '{payments.PublicBaseUrl}' must be an absolute http(s) URL, e.g. https://localhost:7139.");
        }

        foreach (var (client, url) in payments.ReturnUrls)
        {
            if (!IsAbsoluteHttp(url, requireHttps: false))
            {
                throw new InvalidOperationException($"Payments:ReturnUrls:{client} '{url}' must be an absolute http(s) URL.");
            }
        }

        if (payments.IsProvider(PaymentOptions.RazorpayProvider))
        {
            ValidateRazorpay(payments);
        }
    }

    private static void ValidateRazorpay(PaymentOptions payments)
    {
        var missing = payments.Razorpay.MissingKeys;

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Payments:Provider=Razorpay needs {string.Join(", ", missing.Select(k => $"Payments:Razorpay:{k}"))}. " +
                "Run scripts/connect-payments.ps1, or leave Provider=Sandbox to develop without an account.");
        }

        RequirePublicBaseUrl(payments, PaymentOptions.RazorpayProvider);

        // Razorpay's key names its own mode. Disagreeing with Mode is a copy-paste mistake either
        // way: live keys under Test move real money unexpectedly; test keys under Live move none.
        var keyId = payments.Razorpay.KeyId.Trim();

        if (payments.IsLive && keyId.StartsWith("rzp_test_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Payments:Mode=Live but Payments:Razorpay:KeyId is a test key (rzp_test_…).");
        }

        if (!payments.IsLive && keyId.StartsWith("rzp_live_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Payments:Mode=Test but Payments:Razorpay:KeyId is a live key (rzp_live_…). Set Mode=Live deliberately if that is intended.");
        }
    }

    private static void RequirePublicBaseUrl(PaymentOptions payments, string provider)
    {
        if (string.IsNullOrWhiteSpace(payments.PublicBaseUrl))
        {
            throw new InvalidOperationException(
                $"Payments:Provider={provider} needs Payments:PublicBaseUrl — the provider sends the browser back to it. " +
                "In development https://localhost:7139 works for the redirect; a tunnel URL also lets webhooks arrive.");
        }
    }

    private static bool IsAbsoluteHttp(string? url, bool requireHttps) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (!requireHttps && uri.Scheme == Uri.UriSchemeHttp));
}
