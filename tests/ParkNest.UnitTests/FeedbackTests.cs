using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Application.Support;
using ParkNest.Domain.Common;
using ParkNest.Infrastructure.Email;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace ParkNest.UnitTests;

public sealed class FeedbackTests
{
    private readonly RecordingEmailSender _sent = new();
    private readonly TestCurrentUser _user = new();

    private FeedbackService Service(string toAddress = "owner@example.com", params string[] admins) =>
        new(_sent,
            _user,
            new TestClock(new DateTimeOffset(2026, 9, 29, 8, 30, 0, TimeSpan.Zero)),
            MsOptions.Create(new FeedbackOptions { ToAddress = toAddress }),
            MsOptions.Create(new AuthOptions { AdminEmails = admins }));

    [Fact]
    public async Task A_problem_report_is_mailed_to_the_owner_with_the_tester_as_reply_to()
    {
        await Service().SendAsync(new FeedbackInput(
            FeedbackKind.Problem,
            "The pay button does nothing on my phone.",
            Email: "tester@example.com",
            Name: "Asha",
            Page: "/bookings/new",
            Client: "android 1.0.3"));

        var mail = _sent.Messages.Should().ContainSingle().Subject;
        mail.To.Should().Be("owner@example.com");
        mail.ReplyTo.Should().Be("tester@example.com");
        mail.ReplyToName.Should().Be("Asha");
        mail.Subject.Should().Be("[ParkNest beta] Problem: The pay button does nothing on my phone.");
        mail.Text.Should().Contain("Page: /bookings/new")
            .And.Contain("App: android 1.0.3")
            .And.Contain("Account: not signed in")
            .And.Contain("29 Sep 2026, 2:00 PM IST");
    }

    [Fact]
    public async Task A_signed_in_tester_need_not_type_their_email()
    {
        var id = Guid.NewGuid();
        _user.SignIn(id, email: "Renter@Example.com");

        await Service().SendAsync(new FeedbackInput(FeedbackKind.Idea, "Let me save a favourite spot please."));

        var mail = _sent.Messages.Single();
        mail.ReplyTo.Should().Be("renter@example.com");
        mail.Text.Should().Contain($"Account: {id}");
    }

    [Fact]
    public async Task Without_a_configured_inbox_it_goes_to_the_first_admin()
    {
        await Service(toAddress: "", "admin@example.com").SendAsync(
            new FeedbackInput(FeedbackKind.Other, "Nice work on the map view.", Email: "t@example.com"));

        _sent.Messages.Single().To.Should().Be("admin@example.com");
    }

    [Theory]
    [InlineData("too short", "t@example.com")]
    [InlineData("A perfectly reasonable message.", null)]
    [InlineData("A perfectly reasonable message.", "not-an-email")]
    [InlineData("A perfectly reasonable message.", "two words@example.com")]
    public async Task Refuses_what_cannot_be_acted_on(string message, string? email)
    {
        var act = () => Service().SendAsync(new FeedbackInput(FeedbackKind.Problem, message, Email: email));

        await act.Should().ThrowAsync<DomainException>();
        _sent.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task What_the_tester_typed_is_escaped_in_the_html_body()
    {
        await Service().SendAsync(new FeedbackInput(
            FeedbackKind.Problem, "<script>alert(1)</script> broke it", Email: "t@example.com"));

        var html = _sent.Messages.Single().Html;
        html.Should().NotContain("<script>").And.Contain("&lt;script&gt;");
    }

    [Fact]
    public async Task Brevo_sends_it_with_a_reply_to()
    {
        var handler = new CapturingHandler();
        var sender = new BrevoEmailSender(
            new HttpClient(handler),
            MsOptions.Create(new EmailOptions { Provider = "Brevo", ApiKey = "xkeysib-test", FromAddress = "me@gmail.com" }),
            NullLogger<BrevoEmailSender>.Instance);

        await sender.SendAsync(new EmailMessage("owner@example.com", "Subject", "text", "<p>html</p>", "tester@example.com", "Asha"));

        using var json = JsonDocument.Parse(handler.Body!);
        var root = json.RootElement;
        root.GetProperty("to")[0].GetProperty("email").GetString().Should().Be("owner@example.com");
        root.GetProperty("replyTo").GetProperty("email").GetString().Should().Be("tester@example.com");
        root.GetProperty("replyTo").GetProperty("name").GetString().Should().Be("Asha");
        root.GetProperty("sender").GetProperty("email").GetString().Should().Be("me@gmail.com");
    }

    [Fact]
    public async Task A_rejected_brevo_send_tells_the_tester_it_did_not_go()
    {
        var sender = new BrevoEmailSender(
            new HttpClient(new CapturingHandler(HttpStatusCode.BadRequest)),
            MsOptions.Create(new EmailOptions { Provider = "Brevo", ApiKey = "k", FromAddress = "me@gmail.com" }),
            NullLogger<BrevoEmailSender>.Instance);

        var act = () => sender.SendAsync(new EmailMessage("owner@example.com", "s", "t", "h"));

        await act.Should().ThrowAsync<DomainException>();
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<EmailMessage> Messages { get; } = new();

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public CapturingHandler(HttpStatusCode status = HttpStatusCode.Created) => _status = status;

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status);
        }
    }
}
