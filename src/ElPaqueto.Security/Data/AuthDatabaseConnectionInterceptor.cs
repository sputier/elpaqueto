using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ElPaqueto.Security.Data;

/// <summary>
/// Applies the SQLite settings that every connection to the security database needs.
/// </summary>
/// <remarks>
/// WAL lets the server and a concurrent command-line process share the file without failing
/// with <c>SQLITE_BUSY</c> under normal use; the busy timeout makes a writer wait for the
/// other one instead of failing immediately.
/// </remarks>
internal sealed class AuthDatabaseConnectionInterceptor : DbConnectionInterceptor
{
    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
    }

    /// <inheritdoc />
    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Apply(connection);
        return Task.CompletedTask;
    }

    private static void Apply(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite)
        {
            return;
        }

        var timeoutMilliseconds = (int)TimeSpan.FromSeconds(sqlite.DefaultTimeout).TotalMilliseconds;

        using var command = sqlite.CreateCommand();

        // The values are numbers and constants, never user input.
        command.CommandText = $"PRAGMA busy_timeout = {timeoutMilliseconds}; PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
    }
}
