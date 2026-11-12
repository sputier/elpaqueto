using System.Security.Cryptography;
using System.Text;
using ElPaqueto.Security.Auditing;
using ElPaqueto.Security.Tests.Cli;
using ElPaqueto.Security.Tests.Logging;
using Microsoft.Extensions.Logging;

namespace ElPaqueto.Security.Tests.Auditing;

/// <summary>
/// Every <c>admin</c> command, successful or refused, leaves its audit event and never a password or a hash in any
/// log entry (message, structured values, scopes, exception text).
/// </summary>
public sealed class AdminCommandAuditTests : IDisposable
{
    // Unique per run: a match can only come from this test's input.
    private readonly string _password = "Sentinel-Pw-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly string _newPassword = "Sentinel-New-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private readonly AuthDatabaseFixture _database = new();
    private readonly AdminCli _cli;

    public AdminCommandAuditTests()
    {
        _cli = new AdminCli(_database);
    }

    public void Dispose()
    {
        _cli.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task EveryAdminCommand_LogsItsAuditEvent_AndNeverAPasswordOrAHash()
    {
        await RunAsync(0, _password, "admin", "bootstrap", "--username", "root-admin", "--password-stdin");
        await RunAsync(3, _password, "admin", "bootstrap", "--username", "second-admin", "--password-stdin");
        await RunAsync(0, _password, "admin", "user", "create", "alice", "--permissions", "publisher", "--password-stdin");
        await RunAsync(3, _password, "admin", "user", "create", "ALICE", "--permissions", "reader", "--password-stdin");
        await RunAsync(2, null, "admin", "user", "create", "bob", "--permissions", "reader", "--password", _password);
        await RunAsync(2, _password, "admin", "user", "create", _password + "!", "--permissions", "reader", "--password-stdin");
        await RunAsync(0, null, "admin", "user", "list", "--format", "json");
        await RunAsync(0, null, "admin", "user", "show", "alice");
        await RunAsync(0, null, "admin", "user", "set-permissions", "alice", "--permissions", "reader");
        await RunAsync(0, _newPassword, "admin", "user", "reset-password", "alice", "--password-stdin");
        await RunAsync(2, _newPassword + new string('x', 300), "admin", "user", "reset-password", "alice", "--password-stdin");
        await RunAsync(4, _newPassword, "admin", "user", "reset-password", "ghost", "--password-stdin");
        await RunAsync(0, null, "admin", "user", "disable", "alice");
        await RunAsync(0, null, "admin", "user", "enable", "alice");
        await RunAsync(3, null, "admin", "user", "disable", "root-admin");
        await RunAsync(4, null, "admin", "user", "show", "ghost");

        var records = _cli.Recorder.Records;
        var audit = records.Where(r => r.Category == AuditLog.Category).ToList();

        // One event per state change or security-relevant refusal, at Information, under the audit category.
        Assert.Equal(
            [2001, 2002, 2003, 2006, 2007, 2004, 2005, 2008],
            audit.Select(r => r.EventId.Id).ToArray());
        Assert.All(audit, r => Assert.Equal(LogLevel.Information, r.Level));
        Assert.Equal("root-admin", audit[0]["UserName"]);
        Assert.Equal("Read, Publish", audit[3]["OldPermissions"]);
        Assert.Equal("Read", audit[3]["NewPermissions"]);

        var users = await _cli.UsersAsync();
        var secrets = new List<string>
        {
            _password,
            _newPassword,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(_password)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(_newPassword)),
        };
        secrets.AddRange(users.Select(u => u.PasswordHash));
        LogRecords.AssertContainNone(records, [.. secrets]);
    }

    private async Task RunAsync(int expectedExitCode, string? stdin, params string[] arguments)
    {
        var run = await _cli.RunAsync(arguments, stdin);
        Assert.True(run.ExitCode == expectedExitCode, $"{string.Join(' ', arguments.Take(4))}: {run}");
    }
}
