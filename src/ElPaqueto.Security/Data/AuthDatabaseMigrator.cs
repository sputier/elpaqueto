using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ElPaqueto.Security.Data;

internal sealed partial class AuthDatabaseMigrator(
    IServiceScopeFactory scopeFactory,
    ILogger<AuthDatabaseMigrator> logger) : IAuthDatabaseMigrator
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
        if (pending == 0)
        {
            LogUpToDate(logger);
            return;
        }

        LogMigrating(logger, pending);
        await context.Database.MigrateAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The security database is up to date.")]
    private static partial void LogUpToDate(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {Count} pending migration(s) to the security database.")]
    private static partial void LogMigrating(ILogger logger, int count);
}
