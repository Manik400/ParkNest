using System.Globalization;
using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;

namespace ParkNest.Application.Support;

public enum FeedbackKind
{
    Problem,
    Idea,
    Other
}

/// <param name="Email">
/// Where to answer. Optional when signed in with an email account — that address is used — and
/// required otherwise, because a report nobody can reply to is half a report.
/// </param>
/// <param name="Page">Where in the app they were, e.g. <c>/bookings/3f2a…</c>.</param>
/// <param name="Client">Which app and version, e.g. <c>web</c> or <c>android 1.0.3</c>.</param>
public sealed record FeedbackInput(
    FeedbackKind Kind,
    string Message,
    string? Email = null,
    string? Name = null,
    string? Page = null,
    string? Client = null,
    string? UserAgent = null);

public interface IFeedbackService
{
    /// <summary>
    /// Mails a problem report, idea or comment to the platform owner, with the sender as
    /// Reply-To. Open to anyone, signed in or not: a person who cannot sign in is exactly who
    /// needs to report a problem.
    /// </summary>
    Task SendAsync(FeedbackInput input, CancellationToken cancellationToken = default);
}

public sealed class FeedbackService : IFeedbackService
{
    public const int MinMessageLength = 10;
    public const int MaxMessageLength = 2000;

    private const int MaxEmailLength = 254;
    private const int MaxNameLength = 80;
    private const int MaxContextLength = 300;

    // Every timestamp in the email is read by one person in India.
    private static readonly TimeZoneInfo India = FindIndia();

    private readonly IEmailSender _email;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly FeedbackOptions _feedback;
    private readonly AuthOptions _auth;

    public FeedbackService(
        IEmailSender email,
        ICurrentUser currentUser,
        IClock clock,
        IOptions<FeedbackOptions> feedback,
        IOptions<AuthOptions> auth)
    {
        _email = email;
        _currentUser = currentUser;
        _clock = clock;
        _feedback = feedback.Value;
        _auth = auth.Value;
    }

    public async Task SendAsync(FeedbackInput input, CancellationToken cancellationToken = default)
    {
        var message = (input.Message ?? string.Empty).Trim();

        if (message.Length < MinMessageLength)
        {
            throw new DomainException("Tell us a little more — a sentence or two is plenty.");
        }

        if (message.Length > MaxMessageLength)
        {
            throw new DomainException($"Please keep it under {MaxMessageLength} characters.");
        }

        var replyTo = Clip(input.Email, MaxEmailLength) ?? _currentUser.Email;

        if (replyTo is null)
        {
            throw new DomainException("Add your email so we can get back to you.");
        }

        if (!IsEmail(replyTo))
        {
            throw new DomainException("That email address doesn't look right.");
        }

        var to = Recipient() ?? throw new InvalidOperationException(
            "Feedback has nowhere to go. Set Feedback:ToAddress or Auth:AdminEmails.");

        var name = Clip(input.Name, MaxNameLength);
        var context = new List<(string Label, string Value)>
        {
            ("Type", Label(input.Kind)),
            ("From", name is null ? replyTo : $"{name} <{replyTo}>"),
            ("Account", _currentUser.UserId?.ToString() ?? "not signed in"),
            ("Page", Clip(input.Page, MaxContextLength) ?? "—"),
            ("App", Clip(input.Client, MaxContextLength) ?? "—"),
            ("Browser", Clip(input.UserAgent, MaxContextLength) ?? "—"),
            ("Sent", TimeZoneInfo.ConvertTime(_clock.UtcNow, India)
                .ToString("d MMM yyyy, h:mm tt 'IST'", CultureInfo.InvariantCulture))
        };

        await _email.SendAsync(
            new EmailMessage(
                to,
                Subject(input.Kind, message),
                Text(message, context),
                Html(message, context),
                ReplyTo: replyTo,
                ReplyToName: name),
            cancellationToken);
    }

    private string? Recipient()
    {
        if (!string.IsNullOrWhiteSpace(_feedback.ToAddress))
        {
            return _feedback.ToAddress.Trim();
        }

        return _auth.AdminEmails.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e))?.Trim();
    }

    private static string Label(FeedbackKind kind) => kind switch
    {
        FeedbackKind.Problem => "Problem",
        FeedbackKind.Idea => "Idea",
        _ => "Feedback"
    };

    private static string Subject(FeedbackKind kind, string message)
    {
        var firstLine = message.Split('\n', 2)[0].Trim();
        var preview = firstLine.Length > 60 ? firstLine[..57].TrimEnd() + "…" : firstLine;
        return $"[ParkNest beta] {Label(kind)}: {preview}";
    }

    private static string Text(string message, IEnumerable<(string Label, string Value)> context) =>
        message + "\n\n—\n" + string.Join("\n", context.Select(c => $"{c.Label}: {c.Value}"));

    private static string Html(string message, IEnumerable<(string Label, string Value)> context)
    {
        var rows = string.Concat(context.Select(c =>
            $"<tr><td style=\"color:#6b7280;padding:2px 12px 2px 0;vertical-align:top\">{WebUtility.HtmlEncode(c.Label)}</td>" +
            $"<td style=\"padding:2px 0\">{WebUtility.HtmlEncode(c.Value)}</td></tr>"));

        return "<div style=\"font-family:system-ui,sans-serif;font-size:15px;line-height:1.5;color:#111827\">" +
               $"<p style=\"white-space:pre-wrap;margin:0 0 20px\">{WebUtility.HtmlEncode(message)}</p>" +
               $"<table style=\"font-size:13px;border-top:1px solid #e5e7eb;padding-top:12px\">{rows}</table>" +
               "</div>";
    }

    private static string? Clip(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    private static bool IsEmail(string value) =>
        value.Contains('@')
        && !value.Any(char.IsWhiteSpace)
        && MailAddress.TryCreate(value, out var parsed)
        && parsed.Address.Equals(value, StringComparison.OrdinalIgnoreCase);

    private static TimeZoneInfo FindIndia()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "IST", "IST");
        }
    }
}
