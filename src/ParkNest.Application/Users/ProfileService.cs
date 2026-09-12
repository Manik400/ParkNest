using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Abstractions;
using ParkNest.Domain.Common;
using ParkNest.Domain.Users;

namespace ParkNest.Application.Users;

/// <summary>
/// The signed-in account as its owner sees it, and the few fields they may change themselves.
///
/// <c>Phone</c> and <c>Email</c> are sign-in identities and are not editable here: attaching an
/// unverified number to an account would let whoever owns that number sign in as it. The
/// <c>PaymentPhone</c> is different — it is only ever handed to the payment gateway, which insists
/// on one, so it can be typed and saved without a verification step.
/// </summary>
public interface IProfileService
{
    Task<ProfileView> GetMineAsync(CancellationToken cancellationToken = default);

    /// <summary>Null leaves a field alone; an empty string clears the payment phone.</summary>
    Task<ProfileView> UpdateMineAsync(string? fullName, string? paymentPhone, CancellationToken cancellationToken = default);
}

/// <param name="Phone">The sign-in number, if the account has one. Read-only here.</param>
/// <param name="PaymentPhone">The number handed to the payment gateway when <paramref name="Phone"/> is missing.</param>
public sealed record ProfileView(
    Guid Id,
    string FullName,
    string? Phone,
    string? Email,
    string? PaymentPhone,
    UserRole Role,
    KycStatus KycStatus);

public sealed class ProfileService : IProfileService
{
    private readonly IParkNestDbContext _db;
    private readonly ICurrentUser _currentUser;

    public ProfileService(IParkNestDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<ProfileView> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var user = await RequireUserAsync(cancellationToken);
        return View(user);
    }

    public async Task<ProfileView> UpdateMineAsync(string? fullName, string? paymentPhone, CancellationToken cancellationToken = default)
    {
        var user = await RequireUserAsync(cancellationToken);

        if (fullName is not null)
        {
            var name = fullName.Trim();

            if (name.Length is < 2 or > 100)
            {
                throw new DomainException("Your name must be between 2 and 100 characters.");
            }

            user.FullName = name;
        }

        if (paymentPhone is not null)
        {
            user.PaymentPhone = NormalisePhone(paymentPhone);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return View(user);
    }

    /// <summary>Digits only, ten to fifteen of them, the way sign-in stores a number; null clears it.</summary>
    public static string? NormalisePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var digits = new string(phone.Where(char.IsDigit).ToArray());

        if (digits.Length is < 10 or > 15)
        {
            throw new DomainException("That mobile number does not look valid.");
        }

        return digits;
    }

    private async Task<User> RequireUserAsync(CancellationToken cancellationToken)
    {
        var userId = _currentUser.RequireUserId();

        return await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
               ?? throw new UnauthorizedException("The signed-in account no longer exists.");
    }

    private static ProfileView View(User user) =>
        new(user.Id, user.FullName, user.Phone, user.Email, user.PaymentPhone, user.Role, user.KycStatus);
}
