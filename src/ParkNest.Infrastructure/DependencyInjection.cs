using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Auth;
using ParkNest.Application.Listings;
using ParkNest.Application.Options;
using ParkNest.Application.Payments;
using ParkNest.Infrastructure.Auth;
using ParkNest.Infrastructure.Bookings;
using ParkNest.Infrastructure.Caching;
using ParkNest.Infrastructure.Messaging;
using ParkNest.Infrastructure.Notifications;
using ParkNest.Infrastructure.Payments;
using ParkNest.Infrastructure.Storage;
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
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.Configure<PaymentOptions>(configuration.GetSection(PaymentOptions.SectionName));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.Configure<MessagingOptions>(configuration.GetSection(MessagingOptions.SectionName));
        services.Configure<PushOptions>(configuration.GetSection(PushOptions.SectionName));
        services.Configure<KycOptions>(configuration.GetSection(KycOptions.SectionName));
        services.Configure<CacheOptions>(configuration.GetSection(CacheOptions.SectionName));
        services.Configure<AnalyticsOptions>(configuration.GetSection(AnalyticsOptions.SectionName));
        services.Configure<MaintenanceOptions>(configuration.GetSection(MaintenanceOptions.SectionName));

        ValidateAuthOptions(configuration, environment);

        // A button that empties the ledger has no place next to real money. Staging on the free
        // tier is where it is wanted; Production is where it must not exist.
        if (environment.IsProduction()
            && (configuration.GetSection(MaintenanceOptions.SectionName).Get<MaintenanceOptions>()?.AllowDataReset ?? false))
        {
            throw new InvalidOperationException(
                "Maintenance:AllowDataReset cannot be on in Production.");
        }
        ValidateKycOptions(configuration, environment);

        // Either Host=…;Database=… or the postgresql:// URL a hosted provider's dashboard gives.
        var connectionString = PostgresConnectionString.Normalise(
            configuration.GetConnectionString("ParkNest")
            ?? throw new InvalidOperationException("Connection string 'ParkNest' is not configured."));

        services.AddDbContext<ParkNestDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IParkNestDbContext>(sp => sp.GetRequiredService<ParkNestDbContext>());
        AddSpaceSearch(services, configuration);

        // Reports at startup if the database cannot hold the text this application handles.
        // Found the hard way: a Windows-default database landed on WIN1252, which has no rupee
        // sign and no Indian scripts at all.
        services.AddHostedService<DatabaseEncodingCheck>();
        services.AddScoped<Application.Admin.IDataWiper, Persistence.PostgresDataWiper>();

        // AuthService itself is registered by AddParkNestApplication; only its infrastructure
        // collaborators (token signing, OTP delivery) are wired here.
        services.AddScoped<ITokenService, JwtTokenService>();

        AddOtpSenders(services, configuration, environment);
        // The provider switch and its startup validation live in Payments/PaymentRegistration.cs.
        services.AddPaymentGateway(configuration, environment);
        AddPhotoStorage(services, configuration, environment);
        AddMessaging(services, configuration);
        AddPush(services, configuration);

        // Over-runs bill themselves while the session is still running rather than only at
        // checkout, which is the difference between discovering a renter cannot pay while their
        // car is still in the bay and discovering it after they have driven away.
        services.AddHostedService<OverstayMeterService>();

        // Trims the one table that grows with traffic rather than with business.
        services.AddHostedService<Analytics.AnalyticsRetentionSweeper>();

        // Only where orders can actually be created. With payments disabled the sweep would wake
        // every five minutes to scan a table nothing writes to.
        if (!(configuration.GetSection(PaymentOptions.SectionName).Get<PaymentOptions>() ?? new PaymentOptions()).IsDisabled)
        {
            services.AddHostedService<PaymentOrderExpirySweeper>();
        }

        services.AddParkNestApplication();

        return services;
    }

    /// <summary>
    /// At most one sender per sign-in channel. Email over SMTP is the channel that costs nothing,
    /// so it is the one every deployment is expected to have; SMS needs a paid provider, and without
    /// one phone sign-in is switched off rather than faked.
    ///
    /// Development falls back to the logging stand-in on either channel. Production never does —
    /// a sender that prints codes would expose every login — and refuses to start with no real
    /// channel at all.
    /// </summary>
    private static void AddOtpSenders(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var sms = configuration.GetSection(SmsOptions.SectionName).Get<SmsOptions>() ?? new SmsOptions();
        var email = configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new EmailOptions();

        // A provider named but left half-configured is a typo, not a choice to fall back — saying
        // so beats silently printing codes to the console because a password was missing.
        if (sms.IsGateway && !sms.IsGatewayConfigured)
        {
            throw new InvalidOperationException(
                "Sms:Provider=Gateway needs BaseUrl, Username and Password. Leave Provider unset to use the development sender.");
        }

        if (email.IsSmtp && !email.IsSmtpConfigured)
        {
            throw new InvalidOperationException(
                "Email:Provider=Smtp needs Host, a FromAddress or Username, a Password whenever Username is set, " +
                "and Security of StartTls, SslOnConnect or None. Leave Provider unset to use the development sender.");
        }

        if (email.IsBrevo && !email.IsBrevoConfigured)
        {
            throw new InvalidOperationException(
                "Email:Provider=Brevo needs ApiKey and FromAddress (an address verified in Brevo). " +
                "Leave Provider unset to use the development sender.");
        }

        // The Log sender returns the code in the API response, so anyone can sign in as any
        // number or address. That is the point of it on a laptop and a hole anywhere reachable
        // from the internet — a hosted Staging as much as Production. Outside Development a real
        // channel is required, and a channel with no real sender is simply switched off.
        if (!environment.IsDevelopment() && !sms.IsConfigured && !email.IsConfigured)
        {
            throw new InvalidOperationException(
                "No sign-in channel is configured. Set Email:Provider=Smtp or Brevo (both free) or Sms:Provider=Msg91 before deploying outside Development.");
        }

        if (sms.IsMsg91Configured)
        {
            services.AddHttpClient<IOtpSender, Msg91OtpSender>();
        }
        else if (sms.IsGatewayConfigured)
        {
            services.AddHttpClient<IOtpSender, SmsGatewayOtpSender>();
        }
        else if (environment.IsDevelopment())
        {
            AddLoggingSender(services, OtpChannel.Sms);
        }

        if (email.IsBrevoConfigured)
        {
            services.AddHttpClient<IOtpSender, BrevoEmailOtpSender>();
        }
        else if (email.IsSmtpConfigured)
        {
            services.AddScoped<IOtpSender, SmtpEmailOtpSender>();
        }
        else if (environment.IsDevelopment())
        {
            AddLoggingSender(services, OtpChannel.Email);
        }
    }

    private static void AddLoggingSender(IServiceCollection services, OtpChannel channel) =>
        services.AddScoped<IOtpSender>(sp => ActivatorUtilities.CreateInstance<LoggingOtpSender>(sp, channel));

    /// <summary>
    /// How modules hear about each other's events.
    ///
    /// In-process by default, and that is not a stopgap: the modules are in one process, so a
    /// broker between them buys nothing until they are not. Requiring RabbitMQ to run the app
    /// would mean installing a broker to see a booking confirmation.
    /// </summary>
    /// <summary>
    /// Geo-search, with a cache in front of it only when one is configured (PRD §15, Phase 2).
    ///
    /// The decorator is left out of the chain entirely when caching is off rather than registered
    /// and told to stand aside. A disabled cache still sitting in the call path is a class that
    /// nothing exercises until the day somebody switches it on in production.
    /// </summary>
    private static void AddSpaceSearch(IServiceCollection services, IConfiguration configuration)
    {
        var cache = configuration.GetSection(CacheOptions.SectionName).Get<CacheOptions>() ?? new CacheOptions();

        if (!cache.IsEnabled)
        {
            services.AddSingleton<ISearchCache, DisabledSearchCache>();
            services.AddSingleton<ISpaceSearchCacheInvalidator, NoOpSpaceSearchCacheInvalidator>();
            services.AddScoped<ISpaceSearchService, PostgresSpaceSearchService>();
            return;
        }

        if (cache.IsRedis)
        {
            // Singleton because the multiplexer is one long-lived connection that multiplexes
            // every caller; StackExchange.Redis is explicit that creating them per request is the
            // way to exhaust a connection pool.
            services.AddSingleton<ISearchCache, RedisSearchCache>();
        }
        else
        {
            services.AddMemoryCache(options => options.SizeLimit = cache.MemoryEntryLimit);
            services.AddSingleton<ISearchCache, MemorySearchCache>();
        }

        // The query itself, still resolvable on its own — the integration suite asks for the
        // concrete type so it exercises the real SQL rather than whatever a cache remembered.
        services.AddScoped<PostgresSpaceSearchService>();

        services.AddScoped<CachingSpaceSearchService>(sp => new CachingSpaceSearchService(
            sp.GetRequiredService<PostgresSpaceSearchService>(),
            sp.GetRequiredService<ISearchCache>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IOptions<CacheOptions>>(),
            sp.GetRequiredService<ILogger<CachingSpaceSearchService>>()));

        // Both interfaces resolve to the one instance. Two would mean the invalidator bumping a
        // generation that the searcher is not reading.
        services.AddScoped<ISpaceSearchService>(sp => sp.GetRequiredService<CachingSpaceSearchService>());
        services.AddScoped<ISpaceSearchCacheInvalidator>(sp => sp.GetRequiredService<CachingSpaceSearchService>());
    }

    private static void AddMessaging(IServiceCollection services, IConfiguration configuration)
    {
        var messaging = configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>()
            ?? new MessagingOptions();

        if (messaging.IsRabbitMq)
        {
            // Singleton: one connection for the process. Opening one per publish is a TCP
            // handshake and an AMQP negotiation to say that a session ended.
            services.AddSingleton<IEventBus, RabbitMqEventBus>();

            // Only with the broker selected. Alongside the in-process bus this would deliver
            // everything twice — once directly, once round the exchange.
            services.AddHostedService<RabbitMqConsumerService>();
            return;
        }

        services.AddSingleton<IEventBus, InProcessEventBus>();
    }

    /// <summary>
    /// Firebase when a project is configured, nothing otherwise — and unlike SMS or payments,
    /// "nothing" is allowed in Production.
    ///
    /// The durable notification is written either way and the socket still carries the live one,
    /// so a missing Firebase project costs a buzz on a locked handset. Refusing to boot over that
    /// would take the whole platform down to protect a convenience.
    /// </summary>
    private static void AddPush(IServiceCollection services, IConfiguration configuration)
    {
        var push = configuration.GetSection(PushOptions.SectionName).Get<PushOptions>() ?? new PushOptions();

        if (!push.IsConfigured)
        {
            services.AddSingleton<IPushSender, DisabledPushSender>();
            return;
        }

        // Singleton, and that is the point: it caches the OAuth access token minted from the
        // service account key. Per-scope it would sign a fresh JWT and make a round trip to
        // Google for every notification.
        services.AddSingleton(_ => GoogleServiceAccount.FromFile(push.ServiceAccountKeyPath));
        services.AddHttpClient<IPushSender, FirebasePushSender>();
    }

    /// <summary>
    /// Where listing photos go. Local disk is the default because it works on any machine with no
    /// account, and it is honest about its limit: two API instances do not share a directory, so
    /// this is the first thing to replace when the deployment stops being a single box.
    /// </summary>
    private static void AddPhotoStorage(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var storage = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();

        if (storage.IsLocal)
        {
            services.AddSingleton<IPhotoStorage>(sp => new LocalDiskPhotoStorage(
                sp.GetRequiredService<IOptions<StorageOptions>>(),
                environment.ContentRootPath,
                sp.GetRequiredService<ILogger<LocalDiskPhotoStorage>>()));
            return;
        }

        // Not a startup failure even in Production: a deployment may legitimately run without
        // photos, and the endpoints say so plainly rather than the API refusing to boot.
        services.AddSingleton<IPhotoStorage, UnconfiguredPhotoStorage>();
    }

    /// <summary>
    /// The KYC pepper keys the hash of every document number the platform stores.
    ///
    /// Outside Development it must be set and must not be the placeholder: the hash exists so the
    /// same document turning up under two accounts is visible, and an unkeyed or publicly-known
    /// key over a ten-character PAN is a lookup table away from the number itself.
    /// </summary>
    private static void ValidateKycOptions(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        var kyc = configuration.GetSection(KycOptions.SectionName).Get<KycOptions>() ?? new KycOptions();

        if (string.IsNullOrWhiteSpace(kyc.Pepper) || kyc.Pepper.Length < 16
            || kyc.Pepper.Contains("dev-only", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Kyc:Pepper must be set to a real secret of at least 16 characters outside Development.");
        }
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
