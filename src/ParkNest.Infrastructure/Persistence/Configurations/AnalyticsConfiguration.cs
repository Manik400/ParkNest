using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ParkNest.Domain.Analytics;

namespace ParkNest.Infrastructure.Persistence.Configurations;

public sealed class AnalyticsEventConfiguration : IEntityTypeConfiguration<AnalyticsEvent>
{
    public void Configure(EntityTypeBuilder<AnalyticsEvent> builder)
    {
        builder.ToTable("analytics_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.Name).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Source).HasMaxLength(16).IsRequired();
        builder.Property(e => e.VisitorId).HasMaxLength(64);
        builder.Property(e => e.SessionId).HasMaxLength(64);
        builder.Property(e => e.Path).HasMaxLength(256);
        builder.Property(e => e.Referrer).HasMaxLength(256);
        builder.Property(e => e.Detail).HasMaxLength(128);
        builder.Property(e => e.Amount).HasPrecision(18, 2);

        // No foreign key to users on purpose. This table is a tally, not a relation: a row must
        // survive the account it mentions being deleted, and a page view from someone who never
        // signed in has no user to point at.

        // Every query is "this window, by name" — the dashboard reads a date range and the
        // retention sweep deletes by date, so the date leads.
        builder.HasIndex(e => new { e.OccurredAt, e.Name });

        // Unique visitors and the per-day visitor count, which scan the window by visitor.
        builder.HasIndex(e => e.VisitorId);
    }
}
