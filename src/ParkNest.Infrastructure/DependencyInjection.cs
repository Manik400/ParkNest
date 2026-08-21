using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ParkNest.Application;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Auth;
using ParkNest.Application.Listings;
using ParkNest.Application.Options;
using ParkNest.Application.Payments;
using ParkNest.Infrastructure.Auth;
using ParkNest.Infrastructure.Payments;
using ParkNest.Infrastructure.Persistence;

namespace ParkNest.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddParkNestInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.Configure<PlatformOptions>(configuration.GetSection(PlatformOptions.SectionName));
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
        services.Configure<SmsOptions>(configuration.GetSection(SmsOptions.SectionName));
        services.Configure<PaymentOptions>(configuration.GetSection(PaymentOptions.SectionName));

        ValidateAuthOptions(configuration, environment);

        var connectionString = configuration.GetConnectionString("ParkNest")
            ?? throw new InvalidOperationException("Connection string 'ParkNest' is not configured.");

        services.AddDbContext<ParkNestDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IParkNestDbContext>(sp => sp.GetRequiredService<ParkNestDbContext>());
        services.AddScoped<ISpaceSearchService, PostgresSpaceSearchService>();

        // AuthService itself is registered by AddParkNestApplication; only its infrastructure
        // collaborators (token signing, OTP delivery) are wired here.
        services.AddScoped<ITokenService, JwtTokenService>();

        AddSms(services, configuration, environment);
        AddPayments(services, configuration, environment);

        services.AddParkNestApplication();

        return services;
    }

    /// <summary>
    /// Real SMS when configured; the logging stand-in otherwise. Production with neither is a
    /// hard startup failure — falling back to a sender that prints codes would expose every login.
    /// </summary>
    private static void AddSms(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var sms = configuration.GetSection(SmsOptions.SectionName).Get<SmsOptions>() ?? new SmsOptions();

        if (sms.IsConfigured)
        {
            services.AddHttpClient<IOtpSender, Msg91OtpSender>();
            return;
        }

        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "No SMS provider is configured. Set Sms:Provider=Msg91 with AuthKey and TemplateId before deploying to Production.");
        }

        services.AddScoped<IOtpSender, LoggingOtpSender>();
    }

    /// <summary>
    /// Selects the payment gateway. Three providers, in descending order of trust: a real
    /// aggregator, the in-process sandbox, and nothing at all.
    ///
    /// Production accepts only the first. The sandbox signs its own callbacks, so anyone who can
    /// reach its checkout page can mint credits — that is exactly what makes it useful in
    /// development and disqualifying anywhere else.
    /// </summary>
    private static void AddPayments(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var payments = configuration.GetSection(PaymentOptions.SectionName).Get<PaymentOptions>() ?? new PaymentOptions();

        if (payments.IsConfigured)
        {
            services.AddHttpClient<IPaymentGateway, RazorpayPaymentGateway>();
            return;
        }

        if (payments.IsSandbox)
        {
            if (environment.IsProduction())
            {
                throw new InvalidOperationException(
                    "Payments:Provider=Sandbox is a development gateway that issues credits without taking money. Configure a real provider before deploying to Production.");
            }

            // Singleton, and it has to be: when no WebhookSecret is configured the sandbox
            // generates a per-instance signing key, so a scoped registration would sign the
            // checkout page with one key and verify the callback with another.
            services.AddSingleton<SandboxPaymentGateway>();
            services.AddSingleton<IPaymentGateway>(sp => sp.GetRequiredService<SandboxPaymentGateway>());
            return;
        }

        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "No payment gateway is configured. Set Payments:Provider=Razorpay with KeyId, KeySecret and WebhookSecret before deploying to Production.");
        }

        // A stand-in rather than nothing: leaving IPaymentGateway unregistered fails DI validation
        // at startup and takes the entire API down, which is a far worse development experience
        // than the payment endpoints returning a clear "not configured".
        services.AddScoped<IPaymentGateway, UnconfiguredPaymentGateway>();
    }

    /// <summary>
    /// Fails fast on secrets that were never changed from their development placeholders. A weak
    /// or shared signing key means anyone can mint a token for any user.
    /// </summary>
    private static void ValidateAuthOptions(IConfiguration configuration, IHostEnvironment environment)
    {
        var auth = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>()
                   ?? throw new InvalidOperationException("The 'Auth' configuration section is missing.");

        if (string.IsNullOrWhiteSpace(auth.SigningKey) || auth.SigningKey.Length < 32)
        {
            throw new InvalidOperationException(
                "Auth:SigningKey must be set and at least 32 characters.");
        }

        if (string.IsNullOrWhiteSpace(auth.OtpPepper) || auth.OtpPepper.Length < 16)
        {
            throw new InvalidOperationException(
                "Auth:OtpPepper must be set and at least 16 characters.");
        }

        if (environment.IsDevelopment())
        {
            return;
        }

        if (auth.SigningKey.Contains("dev-only", StringComparison.OrdinalIgnoreCase)
            || auth.OtpPepper.Contains("dev-only", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Development placeholder secrets are still configured. Supply real secrets outside Development.");
        }
    }
}
