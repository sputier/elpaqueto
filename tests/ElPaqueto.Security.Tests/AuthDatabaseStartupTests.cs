namespace ElPaqueto.Security.Tests;

/// <summary>
/// Starts the real, built host: the security database must exist before the server accepts
/// requests, and a database that cannot be created must stop the server.
/// </summary>
public sealed class AuthDatabaseStartupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("elpaqueto-startup-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
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

        Assert.True(File.Exists(authDatabase), "The security database was not created.");
        Assert.True(new FileInfo(authDatabase).Length > 0 || File.Exists(authDatabase + "-wal"));
        Assert.DoesNotContain("cannot be created or opened", host.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_WithAnUnwritableSecurityDatabasePath_ExitsNonZeroAndNeverListens()
    {
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, string.Empty);
        var authDatabase = Path.Combine(blocker, "auth.db");

        using var host = HostProcess.Start([], Environment(authDatabase));
        var exitCode = await host.WaitForExitAsync();

        Assert.NotEqual(0, exitCode);
        Assert.Contains("cannot be created or opened", host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Now listening", host.Output, StringComparison.Ordinal);
    }

    private Dictionary<string, string> Environment(string authDatabasePath) => new()
    {
        ["Security__Database__ConnectionString"] = $"Data Source={authDatabasePath}",
        ["Database__ConnectionString"] = $"Data Source={Path.Combine(_root, "packages.db")}",
        ["Storage__Path"] = Path.Combine(_root, "storage"),
    };
}
