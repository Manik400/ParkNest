namespace ParkNest.Application.Abstractions;

/// <param name="ReplyTo">
/// Where a reply should go when it is not the sender — for feedback, the person who wrote it, so
/// answering them is one click in the inbox.
/// </param>
public sealed record EmailMessage(
    string To,
    string Subject,
    string Text,
    string Html,
    string? ReplyTo = null,
    string? ReplyToName = null);

/// <summary>
/// Sends one email through whichever provider <c>Email:Provider</c> names — the same account that
/// sends sign-in codes. Throws <see cref="Domain.Common.DomainException"/> when the provider does
/// not accept the message, so a caller can tell the person it did not go.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
