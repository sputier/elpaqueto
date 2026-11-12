using System.Net;
using System.Net.Sockets;
using ElPaqueto.Security.Passwords;
using Microsoft.Extensions.DependencyInjection;

namespace ElPaqueto.Security.Tests.Cli;

/// <summary>
/// Runs <c>admin</c> commands through the real, built host (<c>ElPaqueto.dll</c>): proves the wiring in
/// <c>Program.cs</c>, the process exit codes, real standard input, and that the commands neither
/// migrate the package database nor start the web server.
/// </summary>
public sealed class AdminCommandProcessTests : IDisposable
{
    private const string Password = "a long enough passphrase";

    private readonly AuthDatabaseFixture _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Bootstrap_OnAnEmptyDataDirectory_CreatesOnlyTheSecurityDatabaseAndNeverBindsAPort()
    {
        // The port the web server would bind is held by the test: had the command tried to start
        // Kestrel, binding would fail and so would the command.
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var environment = HostEnvironment();
        environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";

        using var host = HostProcess.Start(["admin", "bootstrap", "--username", "admin", "--password-stdin"], environment, Password + "\n");
        var exitCode = await host.WaitForExitAsync();

        Assert.True(exitCode == 0, host.Output);
        Assert.Contains("Created administrator 'admin'", host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Now listening", host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, host.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(_database.DatabasePath), "The security database was not created.");
        Assert.False(File.Exists(PackageDatabasePath), "An admin command must not create or migrate the package database.");

        using var cli = new AdminCli(_database);
        var user = Assert.Single(await cli.UsersAsync());
        Assert.Equal(Permission.Read | Permission.Publish | Permission.Admin, user.Permissions);
        Assert.True(user.IsActive);
        Assert.Equal(PasswordVerification.Success, cli.Services.GetRequiredService<IPasswordService>().Verify(user, Password));
    }

    [Fact]
    public async Task Bootstrap_RunTwice_ExitsWithCode3TheSecondTime()
    {
        using (var first = HostProcess.Start(["admin", "bootstrap", "--username", "admin", "--password-stdin"], HostEnvironment(), Password))
        {
            Assert.True(await first.WaitForExitAsync() == 0, first.Output);
        }

        using var second = HostProcess.Start(["admin", "bootstrap", "--username", "admin", "--password-stdin"], HostEnvironment(), Password);

        Assert.Equal(3, await second.WaitForExitAsync());
        Assert.Contains("An administrator already exists", second.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bootstrap_WithAPasswordFile_Succeeds()
    {
        var file = Path.Combine(_database.Directory, "admin_password");
        await File.WriteAllTextAsync(file, Password + "\n");

        using var host = HostProcess.Start(["admin", "bootstrap", "--username", "admin", "--password-file", file], HostEnvironment());

        Assert.True(await host.WaitForExitAsync() == 0, host.Output);
    }

    [Fact]
    public async Task APasswordOption_ExitsWithCode2WithoutEchoingTheValue()
    {
        const string sentinel = "s3ntinel-process-password-value";

        using var host = HostProcess.Start(["admin", "bootstrap", "--username", "admin", "--password", sentinel], HostEnvironment());

        Assert.Equal(2, await host.WaitForExitAsync());
        Assert.DoesNotContain(sentinel, host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("unhandled", host.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(_database.DatabasePath));
    }

    [Fact]
    public async Task WithoutAPasswordSourceAndWithARedirectedInput_ExitsWithCode2InsteadOfBlocking()
    {
        using var host = HostProcess.Start(["admin", "bootstrap", "--username", "admin"], HostEnvironment());

        Assert.Equal(2, await host.WaitForExitAsync(TimeSpan.FromSeconds(60)));
        Assert.Contains("standard input is not a terminal", host.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingOptionValue_IsAUsageErrorNotACrash()
    {
        using var host = HostProcess.Start(["admin", "bootstrap", "--username"], HostEnvironment());

        Assert.Equal(2, await host.WaitForExitAsync());
        Assert.DoesNotContain("unhandled", host.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TwoConcurrentProcesses_ExactlyOneCreatesTheAdministrator()
    {
        // Migrated up front: the concurrency under test is the bootstrap, not the first migration.
        await using (await _database.CreateMigratedContextAsync())
        {
        }

        using var first = HostProcess.Start(["admin", "bootstrap", "--username", "first", "--password-stdin"], HostEnvironment(), Password);
        using var second = HostProcess.Start(["admin", "bootstrap", "--username", "second", "--password-stdin"], HostEnvironment(), Password);

        var exitCodes = await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync());

        Assert.True(
            exitCodes.Order().SequenceEqual([0, 3]),
            $"Exit codes {string.Join(", ", exitCodes)}{Environment.NewLine}{first.Output}{Environment.NewLine}{second.Output}");
        using var cli = new AdminCli(_database);
        Assert.Single(await cli.UsersAsync());
    }

    [Fact]
    public async Task Bootstrap_WhileTheServerRunsOnTheSameData_SucceedsAndTheServerKeepsRunning()
    {
        using var server = HostProcess.Start([], HostEnvironment());
        await server.WaitForOutputAsync("Now listening");

        using var command = HostProcess.Start(["admin", "bootstrap", "--username", "admin", "--password-stdin"], HostEnvironment(), Password);
        var exitCode = await command.WaitForExitAsync();

        Assert.True(exitCode == 0, command.Output);
        Assert.False(server.HasExited, "The server stopped while a command used its security database.");
        using var cli = new AdminCli(_database);
        Assert.Single(await cli.UsersAsync());
    }

    [Fact]
    public async Task UserCommands_WhileTheServerRunsOnTheSameData_ChangeTheUsersAndUseTheExitCodes()
    {
        using var server = HostProcess.Start([], HostEnvironment());
        await server.WaitForOutputAsync("Now listening");

        Assert.Equal(0, await RunAsync(["admin", "bootstrap", "--username", "admin", "--password-stdin"], Password));
        Assert.Equal(0, await RunAsync(["admin", "user", "create", "ci", "--permissions", "publisher", "--password-stdin"], Password));
        Assert.Equal(3, await RunAsync(["admin", "user", "create", "CI", "--permissions", "reader", "--password-stdin"], Password));
        Assert.Equal(2, await RunAsync(["admin", "user", "set-permissions", "ci", "--permissions", "write"]));
        Assert.Equal(0, await RunAsync(["admin", "user", "set-permissions", "ci", "--permissions", "read"]));
        Assert.Equal(0, await RunAsync(["admin", "user", "reset-password", "ci", "--password-stdin"], "another long passphrase"));
        Assert.Equal(0, await RunAsync(["admin", "user", "disable", "ci"]));
        Assert.Equal(3, await RunAsync(["admin", "user", "disable", "admin"]));
        Assert.Equal(4, await RunAsync(["admin", "user", "show", "ghost"]));

        Assert.False(server.HasExited, "The server stopped while commands used its security database.");
        using var cli = new AdminCli(_database);
        var ci = (await cli.UsersAsync()).Single(u => u.UserName == "ci");
        var passwords = cli.Services.GetRequiredService<IPasswordService>();
        Assert.False(ci.IsActive);
        Assert.Equal(Permission.Read, ci.Permissions);
        Assert.Equal(PasswordVerification.Failed, passwords.Verify(new Data.User { IsActive = true, PasswordHash = ci.PasswordHash }, Password));
        Assert.Equal(PasswordVerification.Success, passwords.Verify(new Data.User { IsActive = true, PasswordHash = ci.PasswordHash }, "another long passphrase"));
    }

    [Fact]
    public async Task UserListAsJson_ThroughTheBuiltHost_IsParseableAndHasNoHash()
    {
        Assert.Equal(0, await RunAsync(["admin", "bootstrap", "--username", "admin", "--password-stdin"], Password));
        var environment = HostEnvironment();
        environment["Logging__Console__LogLevel__Default"] = "Warning";

        using var host = HostProcess.Start(["admin", "user", "list", "--format", "json"], environment);
        Assert.Equal(0, await host.WaitForExitAsync());

        using var document = System.Text.Json.JsonDocument.Parse(host.Output);
        var user = Assert.Single(document.RootElement.GetProperty("users").EnumerateArray());
        Assert.Equal("admin", user.GetProperty("userName").GetString());
        Assert.DoesNotContain("hash", host.Output, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int> RunAsync(string[] arguments, string? standardInput = null)
    {
        using var host = HostProcess.Start(arguments, HostEnvironment(), standardInput);
        var exitCode = await host.WaitForExitAsync();
        Assert.DoesNotContain(Password, host.Output, StringComparison.Ordinal);
        return exitCode;
    }

    private string PackageDatabasePath => Path.Combine(_database.Directory, "packages.db");

    private Dictionary<string, string> HostEnvironment() => new()
    {
        ["Security__Database__ConnectionString"] = _database.ConnectionString,

        // The same cost as AdminCli, which verifies the stored hashes: a hash of another cost verifies
        // as SuccessRehashNeeded.
        ["Security__Passwords__IterationCount"] = "100000",
        ["Database__ConnectionString"] = $"Data Source={PackageDatabasePath}",
        ["Storage__Path"] = Path.Combine(_database.Directory, "packages"),
        ["Logging__Console__LogLevel__Default"] = "Information",
    };
}
