using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ParkNest.Domain.Users;

namespace ParkNest.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.FullName).HasMaxLength(200).IsRequired();
        // Either may be null — an account is created by whichever one the user signed in with —
        // and both unique indexes admit any number of nulls.
        builder.Property(u => u.Phone).HasMaxLength(20);
        builder.Property(u => u.Email).HasMaxLength(254);
        builder.Property(u => u.PaymentPhone).HasMaxLength(20);

        builder.HasIndex(u => u.Phone).IsUnique();
        builder.HasIndex(u => u.Email).IsUnique();

        builder.HasMany(u => u.Vehicles)
            .WithOne(v => v.User!)
            .HasForeignKey(v => v.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Ignore(u => u.IsHost);
        builder.Ignore(u => u.IsRenter);
    }
}

public sealed class OtpCodeConfiguration : IEntityTypeConfiguration<OtpCode>
{
    public void Configure(EntityTypeBuilder<OtpCode> builder)
    {
        builder.ToTable("otp_codes");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.Destination).HasMaxLength(254).IsRequired();
        builder.Property(o => o.CodeHash).HasMaxLength(64).IsRequired();

        // Verification looks up the newest unconsumed code for a destination.
        builder.HasIndex(o => new { o.Destination, o.ConsumedAt, o.CreatedAt });

        // Lets a cleanup job drop expired rows cheaply.
        builder.HasIndex(o => o.ExpiresAt);

        builder.Ignore(o => o.IsConsumed);
    }
}

public sealed class VehicleConfiguration : IEntityTypeConfiguration<Vehicle>
{
    public void Configure(EntityTypeBuilder<Vehicle> builder)
    {
        builder.ToTable("vehicles");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.PlateNumber).HasMaxLength(20).IsRequired();
        builder.HasIndex(v => v.PlateNumber);
    }
}

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(t => t.Id);

        // 64 hex characters of SHA-256. Unique, so a presented token resolves to exactly one row.
        builder.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(t => t.TokenHash).IsUnique();

        builder.Property(t => t.RevokedReason).HasMaxLength(200);

        // Revoking a whole family on replay reads by family id.
        builder.HasIndex(t => t.FamilyId);

        // Listing or culling a user's sessions.
        builder.HasIndex(t => new { t.UserId, t.ExpiresAt });
    }
}

public sealed class KycSubmissionConfiguration : IEntityTypeConfiguration<KycSubmission>
{
    public void Configure(EntityTypeBuilder<KycSubmission> builder)
    {
        builder.ToTable("kyc_submissions");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.LegalName).HasMaxLength(200).IsRequired();
        builder.Property(s => s.DocumentLast4).HasMaxLength(8).IsRequired();

        // Hex of a SHA-256 MAC. Fixed width, and never the document number itself.
        builder.Property(s => s.DocumentHash).HasMaxLength(64).IsRequired();
        builder.Property(s => s.DocumentPhotoUrl).HasMaxLength(500);
        builder.Property(s => s.PayoutAccountLast4).HasMaxLength(8);
        builder.Property(s => s.RejectionReason).HasMaxLength(500);

        // The review queue reads "pending, oldest first"; a host's own screen reads their latest.
        builder.HasIndex(s => new { s.Status, s.SubmittedAt });
        builder.HasIndex(s => new { s.UserId, s.SubmittedAt });

        // Not unique — the same document legitimately appears across a host's own resubmissions.
        // The index is what makes "who else has sent this document" cheap enough to show a
        // reviewer on every row of the queue.
        builder.HasIndex(s => s.DocumentHash);
    }
}
