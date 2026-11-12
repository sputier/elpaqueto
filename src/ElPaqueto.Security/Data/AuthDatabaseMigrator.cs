using ElPaqueto.Security.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Data;

internal sealed partial class AuthDatabaseMigrator(
    IServiceScopeFactory scopeFactory,
    IOptions<ElPaquetoSecurityOptions> options,
    ILogger<AuthDatabaseMigrator> logger) : IAuthDatabaseMigrator
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        using (var scope = scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

            var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
            if (pending == 0)
            {
                LogUpToDate(logger);
            }
            else
            {
                LogMigrating(logger, pending);
                await context.Database.MigrateAsync(cancellationToken);
            }
        }

        KeepPrivate(options.Value.Database.ConnectionString);
    }

    // The database holds password hashes: it must not be readable by other users. Files created by
    // the server already are private; a file restored from a backup or created by another tool may not be.
    private void KeepPrivate(string connectionString)
    {
        if (OperatingSystem.IsWindows())
        {
            LogWindowsPermissions(logger);
            return;
        }

        try
        {
            foreach (var path in AuthDatabaseFile.RestrictToOwner(connectionString))
            {
                LogNarrowed(logger, path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogCannotNarrow(logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The security database is up to date.")]
    private static partial void LogUpToDate(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {Count} pending migration(s) to the security database.")]
    private static partial void LogMigrating(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The security database file '{Path}' was readable or writable by other users: its permissions were narrowed to the owner.")]
    private static partial void LogNarrowed(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The permissions of the security database could not be narrowed to the owner. Restrict access to the file and its -wal and -shm files, for example with chmod 600.")]
    private static partial void LogCannotNarrow(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "On Windows the security database inherits the permissions of its folder: restrict the folder to the service account.")]
    private static partial void LogWindowsPermissions(ILogger logger);
}
