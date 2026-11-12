using ElPaqueto.Security.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElPaqueto.Security.Tests;

/// <summary>
/// A real SQLite file in a per-test temporary directory (never an in-memory database, whose
/// behaviour differs for WAL, locking and file permissions).
/// </summary>
public sealed class AuthDatabaseFixture : IDisposable
{
    public AuthDatabaseFixture()
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("elpaqueto-auth-").FullName;
        DatabasePath = Path.Combine(Directory, "db", "auth.db");
        ConnectionString = $"Data Source={DatabasePath}";
    }

    public string Directory { get; }

    public string DatabasePath { get; }

    public string ConnectionString { get; }

    public AuthDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<AuthDbContext>();
        AuthDbContextConfiguration.ConfigureCore(builder, ConnectionString);
        return new AuthDbContext(builder.Options);
    }

    public async Task<AuthDbContext> CreateMigratedContextAsync()
    {
        var context = CreateContext();
        await context.Database.MigrateAsync();
        return context;
    }

    public static User NewUser(string userName, DateTime? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow;
        return new User
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            NormalizedUserName = User.Normalize(userName),
            PasswordHash = "hash-placeholder",
            IsActive = true,
            Permissions = Permission.Read,
            CreatedAt = now,
            UpdatedAt = now,
            PasswordChangedAt = now,
        };
    }

    public void Dispose()
    {
        // Pooled connections keep the file open on Windows.
        SqliteConnection.ClearAllPools();
        System.IO.Directory.Delete(Directory, recursive: true);
    }
}
