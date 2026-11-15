using ElPaqueto.Security.Auditing;
using ElPaqueto.Security.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Data;

internal sealed partial class AuthDatabaseMigrator(
    IServiceScopeFactory scopeFactory,
    IOptions<ElPaquetoSecurityOptions> options,
    ILogger<AuthDatabaseMigrator> logger,
    ILogger<Audit> audit) : IAuthDatabaseMigrator
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        // The file is prepared here, once, and never while serving a request: opening and closing a file
        // handle on the database per request would drop the locks SQLite holds on it (POSIX closes every
        // lock of a process when any descriptor of the file is closed) and would make every request fail
        // when the file is momentarily unavailable.
        AuthDatabaseFile.Prepare(options.Value.Database.ConnectionString);

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
                AuditLog.DatabasePermissionsNarrowed(audit, path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AuditLog.DatabasePermissionsNotNarrowed(audit);
        }
    }

    [LoggerMessage(EventId = 3201, EventName = "SecurityDatabaseUpToDate", Level = LogLevel.Debug,
        Message = "The security database is up to date.")]
    private static partial void LogUpToDate(ILogger logger);

    [LoggerMessage(EventId = 3202, EventName = "SecurityDatabaseMigrating", Level = LogLevel.Information,
        Message = "Applying {Count} pending migration(s) to the security database.")]
    private static partial void LogMigrating(ILogger logger, int count);

    // A hint, not a finding: it is logged on every start on Windows, so it stays out of the audit trail.
    [LoggerMessage(EventId = 3203, EventName = "SecurityDatabasePermissionsInherited", Level = LogLevel.Information,
        Message = "On Windows the security database inherits the permissions of its folder: restrict the folder to the service account.")]
    private static partial void LogWindowsPermissions(ILogger logger);
}
