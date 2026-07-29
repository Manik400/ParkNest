using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ParkNest.Application;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Auth;
using ParkNest.Application.Listings;
using ParkNest.Application.Options;
using ParkNest.Infrastructure.Auth;
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

        ValidateAuthOptions(configuration, environment);

        var connectionString = configuration.GetConnectionString("ParkNest")
            ?? throw new InvalidOperationException("Connection string 'ParkNest' is not configured.");

        services.AddDbContext<ParkNestDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IParkNestDbContext>(sp => sp.GetRequiredService<ParkNestDbContext>());
        services.AddScoped<ISpaceSearchService, PostgresSpaceSearchService>();

        // AuthService itself is registered by AddParkNestApplication; only its infrastructure
        // collaborators (token signing, OTP delivery) are wired here.
        services.AddScoped<ITokenService, JwtTokenService>();

        if (environment.IsProduction())
        {
            // No real SMS gateway is implemented yet. Failing at startup is the correct behaviour:
            // silently falling back to the dev sender would expose every login code.
            throw new InvalidOperationException(
                "No production IOtpSender is configured. Implement an SMS gateway before deploying to Production.");
        }

        services.AddScoped<IOtpSender, LoggingOtpSender>();

        services.AddParkNestApplication();

        return services;
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
