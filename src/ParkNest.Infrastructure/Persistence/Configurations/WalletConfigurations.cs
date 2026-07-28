using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ParkNest.Domain.Wallets;

namespace ParkNest.Infrastructure.Persistence.Configurations;

public sealed class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> builder)
    {
        builder.ToTable("wallets");
        builder.HasKey(w => w.Id);

        builder.HasIndex(w => w.UserId).IsUnique();

        builder.Property(w => w.SpendableBalance).HasPrecision(18, 2);
        builder.Property(w => w.HeldBalance).HasPrecision(18, 2);
        builder.Property(w => w.EarningBalance).HasPrecision(18, 2);

        // Optimistic concurrency: a stale read loses instead of silently overwriting a balance.
        builder.Property(w => w.Version).IsConcurrencyToken();
    }
}

public sealed class LedgerTransactionConfiguration : IEntityTypeConfiguration<LedgerTransaction>
{
    public void Configure(EntityTypeBuilder<LedgerTransaction> builder)
    {
        builder.ToTable("ledger_transactions");
        builder.HasKey(t => t.Id);

        // The database is the real idempotency guard — two concurrent retries cannot both insert.
        builder.Property(t => t.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.HasIndex(t => t.IdempotencyKey).IsUnique();

        builder.HasIndex(t => t.BookingId);
        builder.HasIndex(t => t.CreatedAt);

        builder.Property(t => t.Description).HasMaxLength(500);

        builder.HasMany(t => t.Entries)
            .WithOne(e => e.Transaction!)
            .HasForeignKey(e => e.LedgerTransactionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        builder.ToTable("ledger_entries");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Amount).HasPrecision(18, 2);

        builder.HasIndex(e => new { e.WalletId, e.AccountType });
        builder.HasIndex(e => e.CreatedAt);

        builder.Ignore(e => e.SignedAmount);
        builder.Ignore(e => e.IsSystemAccount);
    }
}
