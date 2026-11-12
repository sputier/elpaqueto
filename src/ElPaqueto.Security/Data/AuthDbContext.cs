using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ElPaqueto.Security.Data;

/// <summary>
/// The security database: users and their API keys. It is separate from the package database
/// and deliberately independent of the inherited <c>IContext</c> pipeline.
/// </summary>
/// <param name="options">The context options.</param>
public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    /// <summary>Name of the migrations history table.</summary>
    public const string MigrationsHistoryTable = "__AuthMigrationsHistory";

    // SQLite cannot order DateTimeOffset values: store UTC DateTime values instead. A value of unspecified
    // kind is taken as UTC, not converted: converting it would depend on the time zone of the server.
    private static readonly ValueConverter<DateTime, DateTime> UtcConverter = new(
        value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc),
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>The users.</summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>The API keys.</summary>
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampChangedEntities();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampChangedEntities();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<User>(user =>
        {
            // Every defined combination of the Permission flags is 0..7: anything else is corrupt data.
            user.ToTable("Users", table => table.HasCheckConstraint("CK_Users_Permissions", "\"Permissions\" BETWEEN 0 AND 7"));
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

        modelBuilder.Entity<ApiKey>(key =>
        {
            key.ToTable("ApiKeys", table =>
            {
                table.HasCheckConstraint("CK_ApiKeys_Permissions", "\"Permissions\" BETWEEN 0 AND 7");
                table.HasCheckConstraint("CK_ApiKeys_SecretHash", "length(\"SecretHash\") = 32");
                table.HasCheckConstraint("CK_ApiKeys_KeyId", "length(\"KeyId\") = 12");
            });
            key.HasKey(k => k.Id);
            key.Property(k => k.Id).ValueGeneratedNever();

            key.Property(k => k.KeyId).IsRequired().HasMaxLength(12);
            key.HasIndex(k => k.KeyId).IsUnique();

            key.Property(k => k.SecretHash).IsRequired().HasMaxLength(ApiKey.SecretHashLength);
            key.Property(k => k.Name).IsRequired().HasMaxLength(ApiKey.MaxNameLength);
            key.Property(k => k.Permissions).HasConversion<int>().IsRequired();

            key.Property(k => k.CreatedAt).HasConversion(UtcConverter).IsRequired();
            key.Property(k => k.ExpiresAt).HasConversion(UtcConverter);
            key.Property(k => k.RevokedAt).HasConversion(UtcConverter);
            key.Property(k => k.LastUsedAt).HasConversion(UtcConverter);

            key.Property(k => k.ConcurrencyStamp).IsRequired().IsConcurrencyToken().HasMaxLength(36);

            // Restrict, not cascade: deleting a user must be a deliberate act that deals with their keys
            // first (revocation is the normal path; disabling the user already invalidates every key).
            key.HasOne(k => k.Owner)
                .WithMany()
                .HasForeignKey(k => k.OwnerUserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private void StampChangedEntities()
    {
        foreach (var entry in ChangeTracker.Entries<User>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.ConcurrencyStamp = Guid.NewGuid().ToString("N");

                // The unique index relies on this value: derive it here so that no caller can forget it.
                entry.Entity.NormalizedUserName = User.Normalize(entry.Entity.UserName);
            }
        }

        foreach (var entry in ChangeTracker.Entries<ApiKey>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            }
        }
    }
}
