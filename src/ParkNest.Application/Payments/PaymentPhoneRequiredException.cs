using ParkNest.Domain.Common;

namespace ParkNest.Application.Payments;

/// <summary>
/// The gateway will not open a checkout without the payer's mobile number, and the account has
/// none on file — the normal case for someone who signed in by email. Its own type, rather than a
/// plain <see cref="DomainException"/>, so a client can tell "ask for a number and try again" apart
/// from "this order is refused": the API surfaces the type name in the problem details.
/// </summary>
public sealed class PaymentPhoneRequiredException : DomainException
{
    public PaymentPhoneRequiredException(string gateway)
        : base($"{gateway} needs a mobile number to take a payment, and your account has none. Enter one to continue; it is saved on your profile for next time.")
    {
    }
}
