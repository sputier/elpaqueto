using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ElPaqueto.Security.Data;

/// <summary>
/// The security database: users now, API keys later. It is separate from the package database
/// and deliberately independent of the inherited <c>IContext</c> pipeline.
/// </summary>
/// <param name="options">The context options.</param>
public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    /// <summary>Name of the migrations history table.</summary>
    public const string MigrationsHistoryTable = "__AuthMigrationsHistory";

    // SQLite cannot order DateTimeOffset values: store UTC DateTime values instead.
    private static readonly ValueConverter<DateTime, DateTime> UtcConverter = new(
        value => value.ToUniversalTime(),
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>The users.</summary>
    public DbSet<User> Users => Set<User>();

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampChangedUsers();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampChangedUsers();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("Users");
            user.HasKey(u => u.Id);
            user.Property(u => u.Id).ValueGeneratedNever();

            user.Property(u => u.UserName).IsRequired().HasMaxLength(User.MaxUserNameLength);
            user.Property(u => u.NormalizedUserName).IsRequired().HasMaxLength(User.MaxUserNameLength);
            user.HasIndex(u => u.NormalizedUserName).IsUnique();

            user.Property(u => u.PasswordHash).IsRequired();
            user.Property(u => u.IsActive).IsRequired();
            user.Property(u => u.Permissions).HasConversion<int>().IsRequired();

            user.Property(u => u.CreatedAt).HasConversion(UtcConverter).IsRequired();
            user.Property(u => u.UpdatedAt).HasConversion(UtcConverter).IsRequired();
            user.Property(u => u.PasswordChangedAt).HasConversion(UtcConverter).IsRequired();

            user.Property(u => u.ConcurrencyStamp).IsRequired().IsConcurrencyToken().HasMaxLength(36);
        });
    }

    private void StampChangedUsers()
    {
        foreach (var entry in ChangeTracker.Entries<User>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            }
        }
    }
}
