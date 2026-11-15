using Microsoft.EntityFrameworkCore;

namespace ElPaqueto.Security.Data;

/// <summary>
/// The one place where <see cref="AuthDbContext"/> is configured, shared by the server and the
/// design-time factory.
/// </summary>
internal static class AuthDbContextConfiguration
{
    public static DbContextOptionsBuilder<AuthDbContext> Configure(
        DbContextOptionsBuilder<AuthDbContext> builder,
        string connectionString)
    {
        ConfigureCore(builder, connectionString);
        return builder;
    }

    public static void ConfigureCore(DbContextOptionsBuilder builder, string connectionString)
    {
        builder
            .UseSqlite(
                connectionString,
                sqlite => sqlite.MigrationsHistoryTable(AuthDbContext.MigrationsHistoryTable))
            .AddInterceptors(new AuthDatabaseConnectionInterceptor());
    }
}
