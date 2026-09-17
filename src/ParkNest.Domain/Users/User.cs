using ParkNest.Domain.Common;

namespace ParkNest.Domain.Users;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public UserRole Role { get; set; } = UserRole.Renter;
    public string FullName { get; set; } = string.Empty;
    /// <summary>
    /// Normalised digits. Null for an account created by email: an account starts with whichever
    /// contact the user signed in with, and at least one of the two is always present.
    /// </summary>
    public string? Phone { get; set; }

    /// <summary>Lower-cased. Null for an account created by phone.</summary>
    public string? Email { get; set; }

    /// <summary>
    /// Normalised digits, unverified. The number handed to the payment gateway when
    /// <see cref="Phone"/> is missing — Cashfree refuses an order without one, and an account
    /// created by email has none. Never used for sign-in: an unverified number that could sign
    /// in would let whoever owns it take the account.
    /// </summary>
    public string? PaymentPhone { get; set; }
    public KycStatus KycStatus { get; set; } = KycStatus.NotStarted;

    /// <summary>0-100 reputation signal. Overstay violations and upheld disputes push it down.</summary>
    public int TrustScore { get; set; } = 100;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Vehicle> Vehicles { get; set; } = new List<Vehicle>();

    public bool IsHost => Role is UserRole.Host or UserRole.Both;
    public bool IsRenter => Role is UserRole.Renter or UserRole.Both;
}
