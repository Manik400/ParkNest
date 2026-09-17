using System.Net.Mail;
using ParkNest.Domain.Common;

namespace ParkNest.Application.Auth;

/// <summary>
/// Where a one-time code goes: a phone number or an email address, told apart by the '@' only an
/// address can contain. Normalised here, once, so a code requested for " Me@Example.com " verifies
/// against "me@example.com" and both reach the same account.
/// </summary>
public readonly record struct OtpDestination(OtpChannel Channel, string Value)
{
    /// <summary>RFC 5321's limit on a forward path; nothing longer can be delivered to.</summary>
    public const int MaxEmailLength = 254;

    public static OtpDestination Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new DomainException("An email address or phone number is required.");
        }

        var trimmed = input.Trim();

        return trimmed.Contains('@') ? ParseEmail(trimmed) : ParsePhone(trimmed);
    }

    private static OtpDestination ParseEmail(string input)
    {
        // Lower-cased whole: the local part is case-sensitive on paper and on no mail provider
        // anybody uses, and two accounts for Me@ and me@ would be the worse failure.
        var email = input.ToLowerInvariant();

        // MailAddress also accepts display-name forms ("Me <me@x.com>"); insisting the parsed
        // address is the whole input rejects those, and the dot rules out intranet hosts.
        if (email.Length > MaxEmailLength
            || !MailAddress.TryCreate(email, out var parsed)
            || parsed.Address != email
            || !parsed.Host.Contains('.'))
        {
            throw new DomainException("That email address does not look valid.");
        }

        return new OtpDestination(OtpChannel.Email, email);
    }

    private static OtpDestination ParsePhone(string input)
    {
        var digits = new string(input.Where(char.IsDigit).ToArray());

        if (digits.Length is < 10 or > 15)
        {
            throw new DomainException("That phone number does not look valid.");
        }

        return new OtpDestination(OtpChannel.Sms, digits);
    }
}
