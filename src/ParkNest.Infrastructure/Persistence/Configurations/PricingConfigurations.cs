using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ParkNest.Domain.Pricing;

namespace ParkNest.Infrastructure.Persistence.Configurations;

public sealed class PricingBandChangeConfiguration : IEntityTypeConfiguration<PricingBandChange>
{
    public void Configure(EntityTypeBuilder<PricingBandChange> builder)
    {
        builder.ToTable("pricing_band_changes");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.City).HasMaxLength(100).IsRequired();
        builder.Property(c => c.Zone).HasMaxLength(100);
        builder.Property(c => c.Reason).HasMaxLength(500);

        builder.Property(c => c.MinPricePerHour).HasPrecision(18, 2);
        builder.Property(c => c.MaxPricePerHour).HasPrecision(18, 2);
        builder.Property(c => c.OverstayMultiplier).HasPrecision(6, 3);
        builder.Property(c => c.PreviousMinPricePerHour).HasPrecision(18, 2);
        builder.Property(c => c.PreviousMaxPricePerHour).HasPrecision(18, 2);
        builder.Property(c => c.PreviousOverstayMultiplier).HasPrecision(6, 3);

        // The two questions this table is read with: "everything that ever happened to this band",
        // and "what changed across the platform last week".
        builder.HasIndex(c => new { c.CityPricingConfigId, c.ChangedAt });
        builder.HasIndex(c => c.ChangedAt);
    }
}
