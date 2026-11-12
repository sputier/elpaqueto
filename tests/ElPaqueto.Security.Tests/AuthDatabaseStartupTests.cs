using Microsoft.Data.Sqlite;

namespace ElPaqueto.Security.Tests;

/// <summary>
/// Starts the real, built host: the security database must exist, with its schema, before the
/// server accepts requests, and a database that cannot be created or opened must stop the server
/// with exit code 1 instead of crashing it.
/// </summary>
public sealed class AuthDatabaseStartupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("elpaqueto-startup-").FullName;

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A just-killed host may still hold a file for a moment: the temp directory is disposable.
        }
    }

    [Fact]
    public async Task Server_CreatesAndMigratesTheSecurityDatabaseBeforeListening()
    {
        var authDatabase = Path.Combine(_root, "auth", "auth.db");

        using var host = HostProcess.Start([], Environment(authDatabase));
        await host.WaitForOutputAsync("Now listening");

        var output = host.Output;
        var migrating = output.IndexOf("pending migration(s) to the security database", StringComparison.Ordinal);
        var listening = output.IndexOf("Now listening", StringComparison.Ordinal);
        Assert.True(migrating >= 0, $"The migration was not logged. Output:{System.Environment.NewLine}{output}");
        Assert.True(migrating < listening, "The security database must be migrated before the server listens.");

        Assert.True(File.Exists(authDatabase), "The security database was not created.");
        await using var connection = new SqliteConnection($"Data Source={authDatabase};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Users'";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Server_WithAnUnwritableSecurityDatabasePath_ExitsWithCodeOneAndNeverListens()
    {
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, string.Empty);
        var authDatabase = Path.Combine(blocker, "auth.db");

        using var host = HostProcess.Start([], Environment(authDatabase));
        var exitCode = await host.WaitForExitAsync();

        // Exit code 1, not a crash: an aborted process can leave a core dump that contains secrets.
        Assert.Equal(1, exitCode);
        Assert.Contains("cannot be created or opened", host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Now listening", host.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_WithModeReadWriteAndAMissingFile_FailsInsteadOfStartingOnAnEmptyDatabase()
    {
        var authDatabase = Path.Combine(_root, "not-mounted", "auth.db");

        using var host = HostProcess.Start([], Environment(authDatabase, ";Mode=ReadWrite"));
        var exitCode = await host.WaitForExitAsync();

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(authDatabase), "Mode=ReadWrite must never create the database.");
        Assert.DoesNotContain("Now listening", host.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Data Source=file:auth.db?mode=rwc")]
    [InlineData("Data Source=:memory:")]
    public async Task Server_RejectsAnInMemoryDatabaseAndAFileUri(string connectionString)
    {
        using var host = HostProcess.Start([], new Dictionary<string, string>
        {
            ["Security__Database__ConnectionString"] = connectionString,
            ["Database__ConnectionString"] = $"Data Source={Path.Combine(_root, "packages.db")}",
            ["Storage__Path"] = Path.Combine(_root, "storage"),
        });

        var exitCode = await host.WaitForExitAsync();

        Assert.Equal(1, exitCode);
        Assert.Contains("Security:Database:ConnectionString", host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Now listening", host.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_WithATypedKeywordThatCannotBeParsed_ReportsAFixedMessageWithoutTheValue()
    {
        const string secretMarker = "s3cr3t-marker";

        using var host = HostProcess.Start([], new Dictionary<string, string>
        {
            ["Security__Database__ConnectionString"] = $"Data Source=auth.db;Default Timeout={secretMarker}",
            ["Database__ConnectionString"] = $"Data Source={Path.Combine(_root, "packages.db")}",
            ["Storage__Path"] = Path.Combine(_root, "storage"),
        });

        var exitCode = await host.WaitForExitAsync();

        Assert.Equal(1, exitCode);
        Assert.Contains("not a valid SQLite connection string", host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(secretMarker, host.Output, StringComparison.Ordinal);
    }

    private Dictionary<string, string> Environment(string authDatabasePath, string connectionStringSuffix = "") => new()
    {
        ["Security__Database__ConnectionString"] = $"Data Source={authDatabasePath}{connectionStringSuffix}",
        ["Database__ConnectionString"] = $"Data Source={Path.Combine(_root, "packages.db")}",
        ["Storage__Path"] = Path.Combine(_root, "storage"),
        ["Logging__Console__LogLevel__Default"] = "Information",
    };
}
