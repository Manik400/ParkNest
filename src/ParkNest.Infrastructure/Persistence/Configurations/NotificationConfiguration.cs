using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ParkNest.Domain.Notifications;

namespace ParkNest.Infrastructure.Persistence.Configurations;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(n => n.Id);

        builder.Property(n => n.Kind).HasMaxLength(64).IsRequired();
        builder.Property(n => n.Title).HasMaxLength(120).IsRequired();
        builder.Property(n => n.Body).HasMaxLength(500).IsRequired();

        // Every read is "this user's, newest first", and the unread count filters on top of it.
        builder.HasIndex(n => new { n.UserId, n.CreatedAt });
        builder.HasIndex(n => new { n.UserId, n.ReadAt });

        builder.Ignore(n => n.IsRead);
    }
}

public sealed class DeviceTokenConfiguration : IEntityTypeConfiguration<DeviceToken>
{
    public void Configure(EntityTypeBuilder<DeviceToken> builder)
    {
        builder.ToTable("device_tokens");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Token).HasMaxLength(512).IsRequired();
        builder.Property(d => d.Platform).HasMaxLength(16).IsRequired();

        // Unique on the token, because the token identifies an install rather than an account.
        // Two rows for one handset would push the same message twice, and would let a device keep
        // receiving a previous signed-in user's bookings.
        builder.HasIndex(d => d.Token).IsUnique();

        // Every send starts with "the tokens for these users".
        builder.HasIndex(d => d.UserId);
    }
}
