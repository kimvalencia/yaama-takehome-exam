using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using UsersService.Domain;

namespace UsersService.Data;

public class UsersDbContext(DbContextOptions<UsersDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<VerificationToken> VerificationTokens => Set<VerificationToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.HasKey(u => u.Id);
            // Keycloak assigns the id; never generate one here.
            e.Property(u => u.Id).ValueGeneratedNever();
            e.Property(u => u.Email).HasMaxLength(User.MaxEmailLength).IsRequired();
            e.Property(u => u.DisplayName).HasMaxLength(User.MaxDisplayNameLength).IsRequired();
            // Emails are lower-cased before saving; the unique index is the last line of defence
            // against concurrent duplicate registrations.
            e.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<VerificationToken>(e =>
        {
            e.ToTable("VerificationTokens");
            e.HasKey(t => t.Id);
            e.Property(t => t.TokenHash).HasMaxLength(64).IsUnicode(false).IsFixedLength().IsRequired();
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>Used only by `dotnet ef` so migrations can be created without the app's DI wiring.</summary>
public sealed class UsersDbContextDesignTimeFactory : IDesignTimeDbContextFactory<UsersDbContext>
{
    public UsersDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<UsersDbContext>()
            .UseSqlServer("Server=localhost;Database=Users;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);
}
