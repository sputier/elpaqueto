using System.Data.Common;
using System.Text.Json;
using ElPaqueto.Security.Data;
using ElPaqueto.Security.Passwords;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace ElPaqueto.Security.Tests.Cli;

public sealed class UserCommandTests : IDisposable
{
    private const string Password = "a long enough passphrase";
    private const string OtherPassword = "another long passphrase";

    private readonly AuthDatabaseFixture _database = new();
    private readonly AdminCli _cli;

    public UserCommandTests()
    {
        _cli = new AdminCli(_database);
    }

    public void Dispose()
    {
        _cli.Dispose();
        _database.Dispose();
    }

    // ---- create ----

    [Theory]
    [InlineData("reader", Permission.Read)]
    [InlineData("publisher", Permission.Read | Permission.Publish)]
    [InlineData("administrator", Permission.Read | Permission.Publish | Permission.Admin)]
    [InlineData("read", Permission.Read)]
    [InlineData("publish", Permission.Publish)]
    [InlineData("admin", Permission.Admin)]
    [InlineData("READ, Publish", Permission.Read | Permission.Publish)]
    [InlineData("reader,admin", Permission.Read | Permission.Admin)]
    public async Task Create_WithPermissionsOrPresets_CreatesAnActiveUser(string permissions, Permission expected)
    {
        var run = await CreateAsync("alice", permissions);

        Assert.True(run.ExitCode == 0, run.ToString());
        var user = Assert.Single(await _cli.UsersAsync());
        Assert.Equal("alice", user.UserName);
        Assert.True(user.IsActive);
        Assert.Equal(expected, user.Permissions);
        Assert.Equal(PasswordVerification.Success, Passwords.Verify(user, Password));
    }

    [Fact]
    public async Task Create_WithAnExistingNameInAnotherCase_ExitsWithCode3()
    {
        await CreateAsync("Alice", "reader");

        var run = await CreateAsync("ALICE", "publisher");

        Assert.Equal(3, run.ExitCode);
        var user = Assert.Single(await _cli.UsersAsync());
        Assert.Equal(Permission.Read, user.Permissions);
    }

    [Theory]
    [InlineData("read,write")]
    [InlineData("owner")]
    [InlineData("read,,publish")]
    [InlineData(",")]
    [InlineData(" ")]
    public async Task Create_WithAnUnknownOrEmptyPermission_ExitsWithCode2ListingTheValidValues(string permissions)
    {
        var run = await CreateAsync("alice", permissions);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("read, publish, admin", run.Error, StringComparison.Ordinal);
        Assert.Contains("administrator", run.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(_database.DatabasePath));
    }

    [Fact]
    public async Task Create_WithoutPermissions_ExitsWithCode2()
    {
        var run = await _cli.RunAsync(["admin", "user", "create", "alice", "--password-stdin"], Password);

        Assert.Equal(2, run.ExitCode);
    }

    [Theory]
    [InlineData("john doe")]
    [InlineData("john:doe")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Create_WithAnInvalidName_ExitsWithCode2(string userName)
    {
        var run = await CreateAsync(userName, "reader");

        Assert.Equal(2, run.ExitCode);
        Assert.False(File.Exists(_database.DatabasePath));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("alice")]
    public async Task Create_WithAPasswordRefusedByThePolicy_ExitsWithCode2(string password)
    {
        var run = await _cli.RunAsync(["admin", "user", "create", "alice", "--permissions", "reader", "--password-stdin"], password);

        Assert.Equal(2, run.ExitCode);
        Assert.False(File.Exists(_database.DatabasePath));
    }

    [Fact]
    public async Task Create_WithAPasswordFileOrAPrompt_Works()
    {
        var file = Path.Combine(_database.Directory, "pw");
        await File.WriteAllTextAsync(file, Password + "\n");
        _cli.Prompt.Answer(OtherPassword, OtherPassword);

        var fromFile = await _cli.RunAsync("admin", "user", "create", "alice", "--permissions", "reader", "--password-file", file);
        var fromPrompt = await _cli.RunAsync(["admin", "user", "create", "bob", "--permissions", "reader"], terminal: true);

        Assert.True(fromFile.ExitCode == 0, fromFile.ToString());
        Assert.True(fromPrompt.ExitCode == 0, fromPrompt.ToString());
        var users = await _cli.UsersAsync();
        Assert.Equal(PasswordVerification.Success, Passwords.Verify(users.Single(u => u.UserName == "alice"), Password));
        Assert.Equal(PasswordVerification.Success, Passwords.Verify(users.Single(u => u.UserName == "bob"), OtherPassword));
    }

    // ---- list / show ----

    [Fact]
    public async Task List_ShowsActiveUsersAndDisabledOnesOnRequest()
    {
        await BootstrapAsync("admin");
        await CreateAsync("bob", "reader");
        await CreateAsync("alice", "publisher");
        Assert.Equal(0, (await _cli.RunAsync("admin", "user", "disable", "bob")).ExitCode);

        var active = await _cli.RunAsync("admin", "user", "list");
        var all = await _cli.RunAsync("admin", "user", "list", "--include-disabled");

        Assert.Equal(0, active.ExitCode);
        Assert.Contains("alice", active.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("bob", active.Out, StringComparison.Ordinal);
        Assert.Contains("bob", all.Out, StringComparison.Ordinal);
        Assert.True(
            all.Out.IndexOf("admin ", StringComparison.Ordinal) < all.Out.IndexOf("alice", StringComparison.Ordinal)
            && all.Out.IndexOf("alice", StringComparison.Ordinal) < all.Out.IndexOf("bob", StringComparison.Ordinal),
            "Users are listed by name.");
    }

    [Fact]
    public async Task ListAsJson_HasTheDocumentedShapeAndNoHash()
    {
        await BootstrapAsync("admin");
        await CreateAsync("bob", "reader");
        await _cli.RunAsync("admin", "user", "disable", "bob");
        var stored = await _cli.UsersAsync();

        var run = await _cli.RunAsync("admin", "user", "list", "--format", "json", "--include-disabled");

        Assert.Equal(0, run.ExitCode);
        using var document = JsonDocument.Parse(run.Out);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(["users"], root.EnumerateObject().Select(p => p.Name));
        var users = root.GetProperty("users").EnumerateArray().ToList();
        Assert.Equal(2, users.Count);
        foreach (var user in users)
        {
            AssertDocumentedUserShape(user);
        }

        var bob = users.Single(u => u.GetProperty("userName").GetString() == "bob");
        Assert.False(bob.GetProperty("isActive").GetBoolean());
        Assert.Equal(["read"], bob.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(stored.Single(u => u.UserName == "bob").Id, bob.GetProperty("id").GetGuid());
        var admin = users.Single(u => u.GetProperty("userName").GetString() == "admin");
        Assert.Equal(["read", "publish", "admin"], admin.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        foreach (var user in stored)
        {
            Assert.DoesNotContain(user.PasswordHash, run.Out, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("hash", run.Out, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShowAsJson_IsOneUserInTheSameShape()
    {
        await BootstrapAsync("admin");

        var run = await _cli.RunAsync("admin", "user", "show", "ADMIN", "--format", "json");

        Assert.Equal(0, run.ExitCode);
        using var document = JsonDocument.Parse(run.Out);
        AssertDocumentedUserShape(document.RootElement);
        Assert.Equal("admin", document.RootElement.GetProperty("userName").GetString());
    }

    [Fact]
    public async Task ShowAsText_ListsTheFields()
    {
        await BootstrapAsync("admin");

        var run = await _cli.RunAsync("admin", "user", "show", "admin");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Permissions:      read, publish, admin", run.Out, StringComparison.Ordinal);
        Assert.Contains("Active:           yes", run.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xml")]
    [InlineData("")]
    public async Task AnInvalidFormat_ExitsWithCode2(string format)
    {
        var run = await _cli.RunAsync("admin", "user", "list", "--format", format);

        Assert.Equal(2, run.ExitCode);
    }

    // ---- unknown users ----

    [Theory]
    [InlineData("show", "ghost")]
    [InlineData("disable", "ghost")]
    [InlineData("enable", "ghost")]
    [InlineData("set-permissions", "ghost", "--permissions", "reader")]
    public async Task AnUnknownUser_ExitsWithCode4(params string[] command)
    {
        await BootstrapAsync("admin");

        var run = await _cli.RunAsync(["admin", "user", .. command]);

        Assert.Equal(4, run.ExitCode);
        Assert.Contains("does not exist", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetPassword_OfAnUnknownUser_ExitsWithCode4()
    {
        await BootstrapAsync("admin");

        var run = await _cli.RunAsync(["admin", "user", "reset-password", "ghost", "--password-stdin"], OtherPassword);

        Assert.Equal(4, run.ExitCode);
    }

    // ---- disable / enable ----

    [Fact]
    public async Task DisableAndEnable_ToggleTheUserAndAreIdempotent()
    {
        await BootstrapAsync("admin");
        await CreateAsync("bob", "reader");
        var created = await UserAsync("bob");

        var disabled = await _cli.RunAsync("admin", "user", "disable", "BOB");
        var afterDisable = await UserAsync("bob");
        var again = await _cli.RunAsync("admin", "user", "disable", "bob");
        var afterAgain = await UserAsync("bob");
        var enabled = await _cli.RunAsync("admin", "user", "enable", "bob");
        var afterEnable = await UserAsync("bob");
        var enabledAgain = await _cli.RunAsync("admin", "user", "enable", "bob");

        Assert.Equal([0, 0, 0, 0], new[] { disabled.ExitCode, again.ExitCode, enabled.ExitCode, enabledAgain.ExitCode });
        Assert.False(afterDisable.IsActive);
        Assert.NotEqual(created.ConcurrencyStamp, afterDisable.ConcurrencyStamp);
        Assert.Contains("already disabled", again.Error, StringComparison.Ordinal);
        Assert.Equal(afterDisable.ConcurrencyStamp, afterAgain.ConcurrencyStamp);
        Assert.True(afterEnable.IsActive);
        Assert.Contains("already enabled", enabledAgain.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisablingTheLastActiveAdministrator_ExitsWithCode3AndChangesNothing()
    {
        await BootstrapAsync("admin");
        await CreateAsync("bob", "publisher");
        var before = await UserAsync("admin");

        var run = await _cli.RunAsync("admin", "user", "disable", "admin");

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("last active", run.Error, StringComparison.Ordinal);
        var after = await UserAsync("admin");
        Assert.True(after.IsActive);
        Assert.Equal(before.ConcurrencyStamp, after.ConcurrencyStamp);
    }

    [Fact]
    public async Task DisablingTheLastActiveAdministrator_IsRefusedEvenIfADisabledAdministratorExists()
    {
        await BootstrapAsync("admin");
        await CreateAsync("second", "administrator");
        Assert.Equal(0, (await _cli.RunAsync("admin", "user", "disable", "second")).ExitCode);

        var run = await _cli.RunAsync("admin", "user", "disable", "admin");

        Assert.Equal(3, run.ExitCode);
    }

    [Fact]
    public async Task WithTwoActiveAdministrators_OneCanBeDisabled()
    {
        await BootstrapAsync("admin");
        await CreateAsync("second", "admin");

        var run = await _cli.RunAsync("admin", "user", "disable", "admin");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.False((await UserAsync("admin")).IsActive);
    }

    // ---- set-permissions ----

    [Fact]
    public async Task SetPermissions_ReplacesThePermissionSet()
    {
        await BootstrapAsync("admin");
        await CreateAsync("bob", "administrator");
        var before = await UserAsync("bob");

        var run = await _cli.RunAsync("admin", "user", "set-permissions", "bob", "--permissions", "publish");

        Assert.True(run.ExitCode == 0, run.ToString());
        var after = await UserAsync("bob");
        Assert.Equal(Permission.Publish, after.Permissions);
        Assert.NotEqual(before.ConcurrencyStamp, after.ConcurrencyStamp);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
    }

    [Fact]
    public async Task SetPermissions_ToTheSameSet_ChangesNothing()
    {
        await BootstrapAsync("admin");
        var before = await UserAsync("admin");

        var run = await _cli.RunAsync("admin", "user", "set-permissions", "admin", "--permissions", "administrator");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(before.ConcurrencyStamp, (await UserAsync("admin")).ConcurrencyStamp);
    }

    [Theory]
    [InlineData("reader")]
    [InlineData("read,publish")]
    public async Task DemotingTheLastActiveAdministrator_ExitsWithCode3AndChangesNothing(string permissions)
    {
        await BootstrapAsync("admin");

        var run = await _cli.RunAsync("admin", "user", "set-permissions", "admin", "--permissions", permissions);

        Assert.Equal(3, run.ExitCode);
        Assert.Equal(Permission.Read | Permission.Publish | Permission.Admin, (await UserAsync("admin")).Permissions);
    }

    [Fact]
    public async Task WithTwoActiveAdministrators_OneCanBeDemoted()
    {
        await BootstrapAsync("admin");
        await CreateAsync("second", "administrator");

        var run = await _cli.RunAsync("admin", "user", "set-permissions", "admin", "--permissions", "reader");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(Permission.Read, (await UserAsync("admin")).Permissions);
    }

    [Fact]
    public async Task TheLastActiveAdministrator_CanKeepAdminWhileOtherFlagsChange()
    {
        await BootstrapAsync("admin");

        var run = await _cli.RunAsync("admin", "user", "set-permissions", "admin", "--permissions", "admin");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(Permission.Admin, (await UserAsync("admin")).Permissions);
    }

    [Fact]
    public async Task ADisabledAdministrator_CanBeDemoted()
    {
        await BootstrapAsync("admin");
        await CreateAsync("second", "administrator");
        await _cli.RunAsync("admin", "user", "disable", "second");

        var run = await _cli.RunAsync("admin", "user", "set-permissions", "second", "--permissions", "reader");

        Assert.True(run.ExitCode == 0, run.ToString());
    }

    [Theory]
    [InlineData("write")]
    [InlineData("")]
    public async Task SetPermissions_WithAnUnknownOrEmptyPermission_ExitsWithCode2(string permissions)
    {
        await BootstrapAsync("admin");

        var run = await _cli.RunAsync("admin", "user", "set-permissions", "admin", "--permissions", permissions);

        Assert.Equal(2, run.ExitCode);
    }

    [Fact]
    public async Task EveryChange_SetsUpdatedAtAndANoOpOrARefusalLeavesIt()
    {
        await BootstrapAsync("admin");
        var created = _cli.Clock.UtcNow;
        await CreateAsync("bob", "reader");

        async Task<DateTime> ChangeAsync(params string[] command)
        {
            _cli.Clock.Advance(TimeSpan.FromMinutes(1));
            var run = await _cli.RunAsync(["admin", "user", .. command], OtherPassword);
            Assert.True(run.ExitCode == 0, run.ToString());
            return _cli.Clock.UtcNow;
        }

        var bob = await UserAsync("bob");
        Assert.Equal(created, bob.CreatedAt);
        Assert.Equal(created, bob.UpdatedAt);

        var disabledAt = await ChangeAsync("disable", "bob");
        Assert.Equal(disabledAt, (await UserAsync("bob")).UpdatedAt);

        await ChangeAsync("disable", "bob");
        Assert.Equal(disabledAt, (await UserAsync("bob")).UpdatedAt);

        var enabledAt = await ChangeAsync("enable", "bob");
        Assert.Equal(enabledAt, (await UserAsync("bob")).UpdatedAt);

        var permissionsAt = await ChangeAsync("set-permissions", "bob", "--permissions", "publisher");
        Assert.Equal(permissionsAt, (await UserAsync("bob")).UpdatedAt);

        await ChangeAsync("set-permissions", "bob", "--permissions", "read,publish");
        Assert.Equal(permissionsAt, (await UserAsync("bob")).UpdatedAt);

        var resetAt = await ChangeAsync("reset-password", "bob", "--password-stdin");
        bob = await UserAsync("bob");
        Assert.Equal(resetAt, bob.UpdatedAt);
        Assert.Equal(resetAt, bob.PasswordChangedAt);
        Assert.Equal(created, bob.CreatedAt);

        _cli.Clock.Advance(TimeSpan.FromMinutes(1));
        var refused = await _cli.RunAsync("admin", "user", "disable", "admin");
        Assert.Equal(3, refused.ExitCode);
        Assert.Equal(created, (await UserAsync("admin")).UpdatedAt);
    }

    // ---- reset-password ----

    [Fact]
    public async Task ResetPassword_OfAnUnknownUser_ExitsWithCode4BeforeAskingForAPassword()
    {
        await BootstrapAsync("admin");

        var weak = await _cli.RunAsync(["admin", "user", "reset-password", "ghost", "--password-stdin"], "short");
        var prompted = await _cli.RunAsync(["admin", "user", "reset-password", "ghost"], terminal: true);

        Assert.Equal(4, weak.ExitCode);
        Assert.Equal(4, prompted.ExitCode);
        Assert.Empty(_cli.Prompt.Prompts);
    }

    [Fact]
    public async Task ResetPassword_ReplacesTheHashAndTheOldPasswordStopsWorking()
    {
        await BootstrapAsync("admin");
        await CreateAsync("bob", "reader");
        var before = await UserAsync("bob");

        var run = await _cli.RunAsync(["admin", "user", "reset-password", "bob", "--password-stdin"], OtherPassword);

        Assert.True(run.ExitCode == 0, run.ToString());
        var after = await UserAsync("bob");
        Assert.Equal(PasswordVerification.Failed, Passwords.Verify(after, Password));
        Assert.Equal(PasswordVerification.Success, Passwords.Verify(after, OtherPassword));
        Assert.NotEqual(before.ConcurrencyStamp, after.ConcurrencyStamp);
        Assert.Contains("not affected", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetPassword_OfADisabledUser_KeepsItDisabled()
    {
        await BootstrapAsync("admin");
        await CreateAsync("bob", "reader");
        await _cli.RunAsync("admin", "user", "disable", "bob");

        var run = await _cli.RunAsync(["admin", "user", "reset-password", "bob", "--password-stdin"], OtherPassword);

        Assert.Equal(0, run.ExitCode);
        Assert.False((await UserAsync("bob")).IsActive);
        Assert.Contains("disabled", run.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("bob")]
    public async Task ResetPassword_WithAPasswordRefusedByThePolicy_ExitsWithCode2AndChangesNothing(string password)
    {
        await BootstrapAsync("admin");
        await CreateAsync("bob", "reader");
        var before = await UserAsync("bob");

        var run = await _cli.RunAsync(["admin", "user", "reset-password", "bob", "--password-stdin"], password);

        Assert.Equal(2, run.ExitCode);
        Assert.Equal(before.PasswordHash, (await UserAsync("bob")).PasswordHash);
    }

    [Fact]
    public async Task ResetPassword_WithoutAPasswordSourceAndWithoutATerminal_ExitsWithCode2()
    {
        await BootstrapAsync("admin");

        var run = await _cli.RunAsync(["admin", "user", "reset-password", "admin"], stdin: OtherPassword, terminal: false);

        Assert.Equal(2, run.ExitCode);
    }

    // ---- abuse ----

    [Theory]
    [InlineData("create", "alice", "--permissions", "reader", "--password", "s3cret-value-misplaced")]
    [InlineData("reset-password", "admin", "--password=s3cret-value-misplaced")]
    [InlineData("reset-password", "admin", "s3cret-value-misplaced")]
    [InlineData("show", "admin", "s3cret-value-misplaced")]
    [InlineData("s3cret-value-misplaced")]
    public async Task PasswordsOnTheCommandLine_AreRejectedWithCode2AndNotEchoed(params string[] command)
    {
        await BootstrapAsync("admin");

        var run = await _cli.RunAsync(["admin", "user", .. command]);

        Assert.Equal(2, run.ExitCode);
        Assert.DoesNotContain("s3cret-value-misplaced", run.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoOutputOrLog_ContainsAPasswordOrAHash()
    {
        const string first = "s3ntinel-user-password-number-one";
        const string second = "s3ntinel-user-password-number-two";
        var runs = new List<CliRun>
        {
            await _cli.RunAsync(["admin", "bootstrap", "--username", "admin", "--password-stdin"], first),
            await _cli.RunAsync(["admin", "user", "create", "bob", "--permissions", "reader", "--password-stdin"], first),
            await _cli.RunAsync(["admin", "user", "create", "bob", "--permissions", "reader", "--password-stdin"], second),
            await _cli.RunAsync(["admin", "user", "reset-password", "bob", "--password-stdin"], second),
            await _cli.RunAsync(["admin", "user", "reset-password", "bob", "--password-stdin"], "bob"),
            await _cli.RunAsync("admin", "user", "set-permissions", "bob", "--permissions", "publisher"),
            await _cli.RunAsync("admin", "user", "disable", "bob"),
            await _cli.RunAsync("admin", "user", "enable", "bob"),
            await _cli.RunAsync("admin", "user", "list", "--include-disabled"),
            await _cli.RunAsync("admin", "user", "list", "--format", "json"),
            await _cli.RunAsync("admin", "user", "show", "bob", "--format", "json"),
            await _cli.RunAsync("admin", "user", "show", "bob"),
        };
        var hashes = (await _cli.UsersAsync()).Select(u => u.PasswordHash).ToList();

        Assert.Equal([0, 0, 3, 0, 2, 0, 0, 0, 0, 0, 0, 0], runs.Select(r => r.ExitCode));
        foreach (var output in runs.Select(r => r.AllOutput).Concat(_cli.Logs))
        {
            Assert.DoesNotContain(first, output, StringComparison.Ordinal);
            Assert.DoesNotContain(second, output, StringComparison.Ordinal);
            foreach (var hash in hashes)
            {
                Assert.DoesNotContain(hash, output, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task EachStateChange_IsLoggedOnceWithItsEventId()
    {
        await BootstrapAsync("admin");
        await CreateAsync("bob", "reader");
        await _cli.RunAsync("admin", "user", "disable", "bob");
        await _cli.RunAsync("admin", "user", "disable", "bob");
        await _cli.RunAsync("admin", "user", "enable", "bob");
        await _cli.RunAsync("admin", "user", "set-permissions", "bob", "--permissions", "publisher");
        await _cli.RunAsync(["admin", "user", "reset-password", "bob", "--password-stdin"], OtherPassword);
        await _cli.RunAsync("admin", "user", "disable", "admin");

        int[] expected = [2001, 2003, 2004, 2005, 2006, 2007, 2008];
        foreach (var id in expected)
        {
            Assert.Single(_cli.Logs, l => l.StartsWith($"{id} ", StringComparison.Ordinal));
        }

        Assert.Contains(_cli.Logs, l => l.StartsWith("2006 ", StringComparison.Ordinal) && l.Contains("NewPermissions=Read, Publish", StringComparison.Ordinal));
    }

    // ---- concurrency ----

    [Fact]
    public async Task TwoConcurrentDisables_OfTheOnlyTwoActiveAdministrators_LeaveOneActive()
    {
        await BootstrapAsync("admin");
        await CreateAsync("second", "administrator");
        var slowCheck = new SlowAdministratorCount();
        using var cli = new AdminCli(_database, slowCheck);

        using var start = new ManualResetEventSlim();
        var first = Task.Run(() =>
        {
            start.Wait();
            return cli.RunAsync("admin", "user", "disable", "admin");
        });
        var second = Task.Run(() =>
        {
            start.Wait();
            return cli.RunAsync("admin", "user", "disable", "second");
        });
        start.Set();
        var runs = await Task.WhenAll(first, second);

        Assert.True(
            runs.Count(r => r.ExitCode == 0) == 1 && runs.Count(r => r.ExitCode == 3) == 1,
            string.Join(Environment.NewLine, runs.Select(r => r.ToString())));
        Assert.Single(await cli.UsersAsync(), u => u.IsActive && (u.Permissions & Permission.Admin) == Permission.Admin);
        Assert.Equal(2, slowCheck.Stalls);
    }

    private IPasswordService Passwords => _cli.Services.GetRequiredService<IPasswordService>();

    private static void AssertDocumentedUserShape(JsonElement user)
    {
        Assert.Equal(
            ["id", "userName", "isActive", "permissions", "createdAt", "updatedAt", "passwordChangedAt"],
            user.EnumerateObject().Select(p => p.Name));
        Assert.True(user.GetProperty("id").TryGetGuid(out _));
        Assert.Equal(JsonValueKind.String, user.GetProperty("userName").ValueKind);
        Assert.True(user.GetProperty("isActive").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.Equal(JsonValueKind.Array, user.GetProperty("permissions").ValueKind);
        foreach (var name in new[] { "createdAt", "updatedAt", "passwordChangedAt" })
        {
            var text = user.GetProperty(name).GetString()!;
            Assert.EndsWith("Z", text, StringComparison.Ordinal);
            Assert.True(user.GetProperty(name).TryGetDateTimeOffset(out var value));
            Assert.Equal(TimeSpan.Zero, value.Offset);
        }
    }

    private async Task<CliRun> CreateAsync(string userName, string permissions)
        => await _cli.RunAsync(["admin", "user", "create", userName, "--permissions", permissions, "--password-stdin"], Password);

    private async Task BootstrapAsync(string userName)
    {
        var run = await _cli.RunAsync(["admin", "bootstrap", "--username", userName, "--password-stdin"], Password);
        Assert.True(run.ExitCode == 0, run.ToString());
    }

    private async Task<User> UserAsync(string userName)
        => (await _cli.UsersAsync()).Single(u => u.UserName == userName);

    /// <summary>Stalls after counting the other active administrators, to widen the race window.</summary>
    private sealed class SlowAdministratorCount : DbCommandInterceptor
    {
        private int _stalls;

        public int Stalls => Volatile.Read(ref _stalls);

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            Stall(command);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Stall(command);
            return ValueTask.FromResult(result);
        }

        private void Stall(DbCommand command)
        {
            if (command.CommandText.Contains("\"Permissions\" & 4", StringComparison.Ordinal)
                && command.CommandText.Contains("\"IsActive\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _stalls);
                Thread.Sleep(TimeSpan.FromMilliseconds(500));
            }
        }
    }
}
