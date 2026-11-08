using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using WalletService.Domain;

namespace WalletService.Data;

public class WalletDbContext(DbContextOptions<WalletDbContext> options) : DbContext(options)
{
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletTransaction> Transactions => Set<WalletTransaction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Wallet>(e =>
        {
            e.ToTable("Wallets");
            e.HasKey(w => w.Id);
            e.Property(w => w.Currency).HasConversion<string>().HasMaxLength(3).IsUnicode(false);
            e.Property(w => w.Balance).HasPrecision(28, 8);
            e.Property(w => w.RowVersion).IsRowVersion();
            e.HasIndex(w => new { w.PlayerId, w.Currency }).IsUnique();
        });

        modelBuilder.Entity<WalletTransaction>(e =>
        {
            e.ToTable("Transactions");
            e.HasKey(t => t.Id);
            e.Property(t => t.Seq).UseIdentityColumn();
            e.Property(t => t.ProviderId).HasMaxLength(100).IsRequired();
            e.Property(t => t.Reference).HasMaxLength(100).IsRequired();
            e.Property(t => t.Type).HasMaxLength(32).IsUnicode(false).IsRequired();
            e.Property(t => t.Amount).HasPrecision(28, 8);
            e.Property(t => t.BalanceAfter).HasPrecision(28, 8);

            e.HasOne<Wallet>().WithMany().HasForeignKey(t => t.WalletId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WalletTransaction>().WithMany().HasForeignKey(t => t.RelatedTransactionId)
                .OnDelete(DeleteBehavior.Restrict);

            // Idempotency: one transaction per provider reference.
            e.HasIndex(t => new { t.ProviderId, t.Reference }).IsUnique();

            // A BET can be settled by at most one WIN; enforced by the database so racing WINs cannot both commit.
            e.HasIndex(t => t.RelatedTransactionId)
                .IsUnique()
                .HasFilter("[Type] = 'WIN' AND [RelatedTransactionId] IS NOT NULL")
                .HasDatabaseName("IX_Transactions_RelatedTransactionId_Win");

            // History per wallet, newest first, cursor on Seq.
            e.HasIndex(t => new { t.WalletId, t.Seq });
        });
    }
}

/// <summary>Used only by `dotnet ef` so migrations can be created without the app's DI wiring.</summary>
public sealed class WalletDbContextDesignTimeFactory : IDesignTimeDbContextFactory<WalletDbContext>
{
    public WalletDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<WalletDbContext>()
            .UseSqlServer("Server=localhost;Database=Wallet;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);
}
