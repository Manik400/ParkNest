using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;
using ParkNest.Infrastructure.Auth;

namespace ParkNest.UnitTests;

public sealed class BrevoEmailOtpSenderTests
{
    private static BrevoEmailOtpSender Sender(StubHandler handler) =>
        new(new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(new EmailOptions
            {
                Provider = "Brevo", ApiKey = "xkeysib-test", FromAddress = "me@gmail.com"
            }),
            Microsoft.Extensions.Options.Options.Create(new AuthOptions { OtpLifetimeMinutes = 5 }),
            NullLogger<BrevoEmailOtpSender>.Instance);

    [Fact]
    public async Task Posts_the_code_to_one_recipient_with_the_api_key()
    {
        var handler = new StubHandler(HttpStatusCode.Created);

        await Sender(handler).SendAsync("renter@example.com", "482915");

        handler.Request!.RequestUri!.ToString().Should().Be("https://api.brevo.com/v3/smtp/email");
        handler.Request.Headers.GetValues("api-key").Should().Equal("xkeysib-test");

        using var json = JsonDocument.Parse(handler.Body!);
        var root = json.RootElement;
        root.GetProperty("sender").GetProperty("email").GetString().Should().Be("me@gmail.com");
        root.GetProperty("to").GetArrayLength().Should().Be(1, "the code goes to exactly one inbox");
        root.GetProperty("to")[0].GetProperty("email").GetString().Should().Be("renter@example.com");
        root.GetProperty("subject").GetString().Should().Be("482915 is your ParkNest sign-in code");
    }

    [Fact]
    public async Task A_rejected_send_is_a_domain_failure_not_a_crash()
    {
        var act = () => Sender(new StubHandler(HttpStatusCode.Unauthorized)).SendAsync("renter@example.com", "123456");

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task An_unreachable_api_is_a_domain_failure_not_a_crash()
    {
        var act = () => Sender(new StubHandler(new HttpRequestException("no route"))).SendAsync("renter@example.com", "123456");

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public void Brevo_needs_a_key_and_a_verified_sender()
    {
        new EmailOptions { Provider = "Brevo", ApiKey = "k", FromAddress = "me@gmail.com" }.IsConfigured.Should().BeTrue();
        new EmailOptions { Provider = "Brevo", FromAddress = "me@gmail.com" }.IsBrevoConfigured.Should().BeFalse();
        new EmailOptions { Provider = "Brevo", ApiKey = "k" }.IsBrevoConfigured.Should().BeFalse();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly Exception? _failure;

        public StubHandler(HttpStatusCode status) => _status = status;

        public StubHandler(Exception failure) => _failure = failure;

        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_failure is not null)
            {
                throw _failure;
            }

            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status) { Content = new StringContent("{}") };
        }
    }
}
