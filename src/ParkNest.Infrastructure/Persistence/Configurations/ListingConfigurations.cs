using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ParkNest.Domain.Listings;

namespace ParkNest.Infrastructure.Persistence.Configurations;

public sealed class ParkingSpaceConfiguration : IEntityTypeConfiguration<ParkingSpace>
{
    public void Configure(EntityTypeBuilder<ParkingSpace> builder)
    {
        builder.ToTable("parking_spaces");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Title).HasMaxLength(200).IsRequired();
        builder.Property(s => s.AddressLine).HasMaxLength(500).IsRequired();
        builder.Property(s => s.City).HasMaxLength(100).IsRequired();
        builder.Property(s => s.Zone).HasMaxLength(100);
        builder.Property(s => s.PricePerHour).HasPrecision(18, 2);

        // Defaulted at the database so the backfill on existing rows lands on a real zone rather
        // than an empty string, which would fail every availability check.
        builder.Property(s => s.TimeZoneId)
            .HasMaxLength(64)
            .HasDefaultValue("Asia/Kolkata")
            .IsRequired();

        builder.HasIndex(s => new { s.City, s.Status });

        // The PostGIS geography column and its GiST index are added by migration SQL and kept in
        // sync by a trigger — see docs/adr/0003-geo-search-without-nts.md.
        builder.HasMany(s => s.SupportedVehicleTypes)
            .WithOne()
            .HasForeignKey(v => v.ParkingSpaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.AvailabilityWindows)
            .WithOne()
            .HasForeignKey(w => w.ParkingSpaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.Photos)
            .WithOne()
            .HasForeignKey(p => p.ParkingSpaceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SpaceVehicleSupportConfiguration : IEntityTypeConfiguration<SpaceVehicleSupport>
{
    public void Configure(EntityTypeBuilder<SpaceVehicleSupport> builder)
    {
        builder.ToTable("space_vehicle_supports");
        builder.HasKey(v => v.Id);
        builder.HasIndex(v => new { v.ParkingSpaceId, v.VehicleType }).IsUnique();
    }
}

public sealed class SpacePhotoConfiguration : IEntityTypeConfiguration<SpacePhoto>
{
    public void Configure(EntityTypeBuilder<SpacePhoto> builder)
    {
        builder.ToTable("space_photos");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Url).HasMaxLength(1000).IsRequired();
    }
}

public sealed class AvailabilityWindowConfiguration : IEntityTypeConfiguration<AvailabilityWindow>
{
    public void Configure(EntityTypeBuilder<AvailabilityWindow> builder)
    {
        builder.ToTable("availability_windows");
        builder.HasKey(w => w.Id);
        builder.HasIndex(w => new { w.ParkingSpaceId, w.DayOfWeek });
    }
}

public sealed class AvailabilityBlackoutConfiguration : IEntityTypeConfiguration<AvailabilityBlackout>
{
    public void Configure(EntityTypeBuilder<AvailabilityBlackout> builder)
    {
        builder.ToTable("availability_blackouts");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Reason).HasMaxLength(300);
        builder.HasIndex(b => new { b.ParkingSpaceId, b.From, b.To });
    }
}
