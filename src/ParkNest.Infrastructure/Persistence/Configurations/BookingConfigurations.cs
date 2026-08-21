using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ParkNest.Domain.Bookings;
using ParkNest.Domain.Disputes;
using ParkNest.Domain.Payments;
using ParkNest.Domain.Payouts;
using ParkNest.Domain.Pricing;
using ParkNest.Domain.Ratings;

namespace ParkNest.Infrastructure.Persistence.Configurations;

public sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.ToTable("bookings");
        builder.HasKey(b => b.Id);

        foreach (var money in new[]
                 {
                     nameof(Booking.RatePerHour), nameof(Booking.HoldAmount), nameof(Booking.OverstayAmount),
                     nameof(Booking.SettledAmount), nameof(Booking.PlatformFee), nameof(Booking.ShortfallAmount)
                 })
        {
            builder.Property<decimal>(money).HasPrecision(18, 2);
        }

        builder.Property(b => b.OverstayMultiplier).HasPrecision(6, 3);

        // Drives both "my bookings" lists and the overlap check on a new booking.
        builder.HasIndex(b => new { b.ParkingSpaceId, b.StartTime, b.ExpectedEndTime });
        builder.HasIndex(b => new { b.RenterId, b.Status });
        builder.HasIndex(b => new { b.HostId, b.Status });

        builder.Ignore(b => b.IsSettled);
    }
}

public sealed class CityPricingConfigConfiguration : IEntityTypeConfiguration<CityPricingConfig>
{
    public void Configure(EntityTypeBuilder<CityPricingConfig> builder)
    {
        builder.ToTable("city_pricing_configs");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.City).HasMaxLength(100).IsRequired();
        builder.Property(c => c.Zone).HasMaxLength(100);
        builder.Property(c => c.MinPricePerHour).HasPrecision(18, 2);
        builder.Property(c => c.MaxPricePerHour).HasPrecision(18, 2);
        builder.Property(c => c.OverstayMultiplier).HasPrecision(6, 3);

        builder.HasIndex(c => new { c.City, c.Zone, c.VehicleType }).IsUnique();
    }
}

public sealed class PayoutConfiguration : IEntityTypeConfiguration<Payout>
{
    public void Configure(EntityTypeBuilder<Payout> builder)
    {
        builder.ToTable("payouts");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Amount).HasPrecision(18, 2);
        builder.Property(p => p.BankDetailsRef).HasMaxLength(200);
        builder.Property(p => p.ProviderReference).HasMaxLength(200);
        builder.Property(p => p.FailureReason).HasMaxLength(500);

        builder.HasIndex(p => new { p.HostId, p.Status });
    }
}

public sealed class PaymentOrderConfiguration : IEntityTypeConfiguration<PaymentOrder>
{
    public void Configure(EntityTypeBuilder<PaymentOrder> builder)
    {
        builder.ToTable("payment_orders");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.Amount).HasPrecision(18, 2);
        builder.Property(o => o.Currency).HasMaxLength(3).IsRequired();
        builder.Property(o => o.ProviderOrderId).HasMaxLength(120).IsRequired();
        builder.Property(o => o.ProviderPaymentId).HasMaxLength(120);
        builder.Property(o => o.FailureReason).HasMaxLength(500);

        // Webhook lookup is by the gateway's id, and it must resolve to exactly one order.
        builder.HasIndex(o => o.ProviderOrderId).IsUnique();
        builder.HasIndex(o => new { o.UserId, o.Status });

        builder.Ignore(o => o.IsSettled);
    }
}

public sealed class DisputeConfiguration : IEntityTypeConfiguration<Dispute>
{
    public void Configure(EntityTypeBuilder<Dispute> builder)
    {
        builder.ToTable("disputes");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Reason).HasMaxLength(1000).IsRequired();
        builder.Property(d => d.Resolution).HasMaxLength(2000);

        builder.HasIndex(d => new { d.BookingId, d.Status });

        builder.HasMany(d => d.Evidence)
            .WithOne()
            .HasForeignKey(e => e.DisputeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DisputeEvidenceConfiguration : IEntityTypeConfiguration<DisputeEvidence>
{
    public void Configure(EntityTypeBuilder<DisputeEvidence> builder)
    {
        builder.ToTable("dispute_evidence");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Url).HasMaxLength(1000).IsRequired();
        builder.Property(e => e.Note).HasMaxLength(1000);
    }
}

public sealed class RatingConfiguration : IEntityTypeConfiguration<Rating>
{
    public void Configure(EntityTypeBuilder<Rating> builder)
    {
        builder.ToTable("ratings");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Comment).HasMaxLength(1000);

        // One rating per direction per booking.
        builder.HasIndex(r => new { r.BookingId, r.FromUserId }).IsUnique();
        builder.HasIndex(r => r.ToUserId);
    }
}
