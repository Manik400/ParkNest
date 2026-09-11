using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;
using ParkNest.Infrastructure.Auth;

namespace ParkNest.UnitTests;

/// <summary>
/// The self-hosted gateway path.
///
/// What is worth defending here is the boundary rather than the happy path: the code must reach
/// exactly one handset in a form a gateway will dial, and every way the gateway can fail — a flat
/// battery, a rejected request, a phone that left the network — has to arrive as a domain failure
/// the caller can show a user, never as the code leaking or an unhandled exception.
/// </summary>
public sealed class SmsGatewayOtpSenderTests
{
    private static SmsOptions Options() => new()
    {
        Provider = "Gateway",
        BaseUrl = "http://192.168.1.20:8080",
        Username = "parknest",
        Password = "secret",
        DefaultCountryCode = "91"
    };

    private static SmsGatewayOtpSender Sender(StubHandler handler, SmsOptions? options = null) =>
        new(new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(options ?? Options()),
            NullLogger<SmsGatewayOtpSender>.Instance);

    [Fact]
    public async Task Posts_the_code_to_one_recipient_in_E164()
    {
        var handler = new StubHandler(HttpStatusCode.Accepted);

        await Sender(handler).SendAsync("9000000001", "123456");

        var body = JsonDocument.Parse(handler.LastBody!).RootElement;

        body.GetProperty("message").GetString().Should().Contain("123456");

        var recipients = body.GetProperty("phoneNumbers").EnumerateArray().Select(p => p.GetString()).ToList();

        // One number, and the country code the gateway needs to dial it. A bare ten digits would
        // be dialled against whatever network the gateway handset happens to be on.
        recipients.Should().ContainSingle().Which.Should().Be("+919000000001");
    }

    [Fact]
    public async Task An_already_qualified_number_is_left_alone()
    {
        var handler = new StubHandler(HttpStatusCode.Accepted);

        await Sender(handler).SendAsync("+447700900123", "123456");

        var body = JsonDocument.Parse(handler.LastBody!).RootElement;

        body.GetProperty("phoneNumbers").EnumerateArray().Single().GetString()
            .Should().Be("+447700900123");
    }

    [Fact]
    public async Task Sends_to_the_message_endpoint_with_basic_auth()
    {
        var handler = new StubHandler(HttpStatusCode.Accepted);

        await Sender(handler).SendAsync("9000000001", "123456");

        handler.LastRequest!.RequestUri!.ToString().Should().Be("http://192.168.1.20:8080/message");
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Basic");
    }

    [Fact]
    public async Task A_rejected_send_surfaces_as_a_domain_failure()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, "bad credentials");

        var send = () => Sender(handler).SendAsync("9000000001", "123456");

        // The user is told to try again; what the gateway said, and that there is a gateway at
        // all, stays in the log.
        (await send.Should().ThrowAsync<DomainException>())
            .Which.Message.Should().NotContain("credentials");
    }

    [Fact]
    public async Task An_unreachable_gateway_surfaces_as_a_domain_failure()
    {
        // The gateway is a phone on a desk: flat batteries and dropped Wi-Fi are its normal
        // failure modes, not exceptional ones.
        var handler = new StubHandler(new HttpRequestException("no route to host"));

        var send = () => Sender(handler).SendAsync("9000000001", "123456");

        await send.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public void The_code_is_never_returned_in_the_response()
    {
        Sender(new StubHandler(HttpStatusCode.Accepted)).ExposesCodeInResponse.Should().BeFalse();
    }

    [Fact]
    public async Task The_message_body_follows_the_configured_template()
    {
        var handler = new StubHandler(HttpStatusCode.Accepted);
        var options = Options();
        options.MessageTemplate = "ParkNest: {code}. Valid 5 minutes.";

        await Sender(handler, options).SendAsync("9000000001", "654321");

        JsonDocument.Parse(handler.LastBody!).RootElement.GetProperty("message").GetString()
            .Should().Be("ParkNest: 654321. Valid 5 minutes.");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly HttpRequestException? _throw;

        public StubHandler(HttpStatusCode status, string body = "{}")
        {
            _status = status;
            _body = body;
        }

        public StubHandler(HttpRequestException toThrow)
        {
            _throw = toThrow;
            _status = HttpStatusCode.OK;
            _body = "{}";
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (_throw is not null)
            {
                throw _throw;
            }

            return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
        }
    }
}
