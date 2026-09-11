using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ParkNest.Application.Options;
using ParkNest.Application.Payments;
using ParkNest.Infrastructure.Payments;

namespace ParkNest.UnitTests;

/// <summary>
/// Which gateway a configuration produces, and which configurations never get as far as starting.
///
/// The previous wiring registered Razorpay whenever keys were present, whatever
/// <c>Payments:Provider</c> said — so these pin down that the setting is actually obeyed, and that
/// every way of moving money wrongly (the sandbox or test keys in Production, a half-filled
/// section, an unknown provider) stops the application at startup instead of at the first recharge.
/// </summary>
public sealed class PaymentRegistrationTests
{
    private static IServiceProvider Build(string environment, params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => $"Payments:{s.Key}", s => (string?)s.Value))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<PaymentOptions>(configuration.GetSection(PaymentOptions.SectionName));
        services.AddSingleton<PaymentUrls>();
        services.AddPaymentGateway(configuration, new TestHostEnvironment(environment));

        return services.BuildServiceProvider();
    }

    private static readonly (string, string)[] Razorpay =
    {
        ("Provider", "Razorpay"),
        ("PublicBaseUrl", "https://localhost:7139"),
        ("Razorpay:KeyId", "rzp_test_abc"),
        ("Razorpay:KeySecret", "secret"),
        ("Razorpay:WebhookSecret", "whsec")
    };

    private static (string, string)[] With(params (string, string)[] overrides) =>
        Razorpay.Where(s => overrides.All(o => o.Item1 != s.Item1)).Concat(overrides).ToArray();

    private static IPaymentGateway Gateway(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IPaymentGateway>();
    }

    [Fact]
    public void No_provider_in_development_starts_with_payments_switched_off()
    {
        Gateway(Build("Development")).Should().BeOfType<UnconfiguredPaymentGateway>();
    }

    [Fact]
    public void The_sandbox_is_one_instance_so_it_verifies_with_the_key_it_signed_with()
    {
        var provider = Build("Development", ("Provider", "Sandbox"));

        var first = Gateway(provider);
        var second = Gateway(provider);

        first.Should().BeOfType<SandboxPaymentGateway>();
        second.Should().BeSameAs(first);
    }

    [Fact]
    public void A_configured_razorpay_is_what_you_get()
    {
        Gateway(Build("Development", Razorpay)).Should().BeOfType<RazorpayPaymentGateway>();
    }

    [Theory]
    [InlineData("Stripe")]
    [InlineData("PayPal")]
    public void An_unknown_provider_refuses_to_start_even_with_keys(string provider)
    {
        var act = () => Build("Development", With(("Provider", provider)));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{provider}*not supported*");
    }

    [Fact]
    public void A_half_configured_provider_names_the_missing_key()
    {
        var act = () => Build("Development", With(("Razorpay:WebhookSecret", "")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Payments:Razorpay:WebhookSecret*");
    }

    [Fact]
    public void A_real_provider_needs_a_public_base_url_to_send_the_browser_back_to()
    {
        var act = () => Build("Development", With(("PublicBaseUrl", "")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*PublicBaseUrl*");
    }

    [Fact]
    public void A_live_key_under_test_mode_is_refused()
    {
        var act = () => Build("Development", With(("Razorpay:KeyId", "rzp_live_abc")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*live key*");
    }

    [Fact]
    public void The_sandbox_is_refused_in_production()
    {
        var act = () => Build("Production", ("Provider", "Sandbox"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Sandbox*");
    }

    [Fact]
    public void No_provider_is_refused_in_production()
    {
        var act = () => Build("Production");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Test_mode_is_refused_in_production()
    {
        var act = () => Build("Production", Razorpay);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Mode=Test*");
    }

    [Fact]
    public void An_http_public_address_is_refused_in_production()
    {
        var act = () => Build("Production", With(
            ("Mode", "Live"),
            ("Razorpay:KeyId", "rzp_live_abc"),
            ("PublicBaseUrl", "http://api.parknest.test")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*https*");
    }

    [Fact]
    public void Live_razorpay_on_https_starts_in_production()
    {
        var provider = Build("Production", With(
            ("Mode", "Live"),
            ("Razorpay:KeyId", "rzp_live_abc"),
            ("PublicBaseUrl", "https://api.parknest.test")));

        Gateway(provider).Should().BeOfType<RazorpayPaymentGateway>();
    }

    [Fact]
    public void A_key_left_over_from_the_flat_layout_refuses_to_start()
    {
        // Silently ignoring it is how a deployment ends up on the sandbox while its operator
        // believes it is live.
        var act = () => Build("Development", ("Provider", "Sandbox"), ("KeyId", "rzp_test_abc"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Payments:KeyId*moved*");
    }

    [Fact]
    public void A_return_url_that_is_not_absolute_refuses_to_start()
    {
        var act = () => Build("Development", ("Provider", "Sandbox"), ("ReturnUrls:admin", "/wallet"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*ReturnUrls:admin*");
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string environmentName) => EnvironmentName = environmentName;

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "ParkNest.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>Where the browser is sent, and that nothing a request carries can choose it.</summary>
public sealed class PaymentUrlsTests
{
    private static PaymentUrls Urls(string publicBaseUrl = "") =>
        new(Options.Create(new PaymentOptions { PublicBaseUrl = publicBaseUrl }));

    [Fact]
    public void Without_a_public_address_the_urls_are_relative_to_this_api()
    {
        var orderId = Guid.NewGuid();

        Urls().ReturnUrl(orderId).Should().Be($"/checkout/return/{orderId}");
        Urls().WebhookUrl.Should().BeNull("nothing outside could reach it");
    }

    [Fact]
    public void With_a_public_address_the_urls_are_absolute()
    {
        var urls = Urls("https://api.parknest.test/");

        urls.CheckoutUrl("order_1").Should().Be("https://api.parknest.test/checkout/order_1");
        urls.WebhookUrl.Should().Be("https://api.parknest.test/api/payments/webhook");
    }

    [Fact]
    public void The_admin_site_gets_its_wallet_page_with_the_order_to_poll()
    {
        var orderId = Guid.NewGuid();

        Urls().ClientReturnUrl("admin", orderId).Should().Be($"http://localhost:4200/wallet?orderId={orderId}");
        Urls().ClientReturnUrl(null, orderId).Should().Be($"http://localhost:4200/wallet?orderId={orderId}");
    }

    [Theory]
    [InlineData("app")]
    [InlineData("https://evil.example")]
    [InlineData("somewhere-else")]
    public void Anything_but_a_listed_client_gets_no_redirect(string returnTo)
    {
        Urls().ClientReturnUrl(returnTo, Guid.NewGuid()).Should().BeNull();
    }
}
