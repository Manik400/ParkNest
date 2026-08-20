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
