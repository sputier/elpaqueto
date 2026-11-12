using System.Data.Common;
using ElPaqueto.Security.Passwords;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace ElPaqueto.Security.Tests.Cli;

public sealed class BootstrapCommandTests : IDisposable
{
    private const string Password = "a long enough passphrase";

    private readonly AuthDatabaseFixture _database = new();
    private readonly AdminCli _cli;

    public BootstrapCommandTests()
    {
        _cli = new AdminCli(_database);
    }

    public void Dispose()
    {
        _cli.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task OnAnEmptyDataDirectory_CreatesTheDatabaseAndOneActiveAdministrator()
    {
        Assert.False(File.Exists(_database.DatabasePath));

        var run = await _cli.RunAsync(["admin", "bootstrap", "--username", "admin", "--password-stdin"], Password + "\n");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.True(File.Exists(_database.DatabasePath));
        var user = Assert.Single(await _cli.UsersAsync());
        Assert.Equal("admin", user.UserName);
        Assert.Equal("ADMIN", user.NormalizedUserName);
        Assert.True(user.IsActive);
        Assert.Equal(Permission.Read | Permission.Publish | Permission.Admin, user.Permissions);
        Assert.Equal(user.CreatedAt, user.UpdatedAt);
        Assert.Equal(user.CreatedAt, user.PasswordChangedAt);
        Assert.InRange(user.CreatedAt, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));
        Assert.Contains("Created administrator 'admin'", run.Out, StringComparison.Ordinal);
        Assert.Empty(run.Error);
    }

    [Fact]
    public async Task TheStoredHash_VerifiesTheGivenPasswordThroughThePasswordService()
    {
        await BootstrapAsync("admin");

        var user = Assert.Single(await _cli.UsersAsync());
        var passwords = _cli.Services.GetRequiredService<IPasswordService>();

        Assert.NotEqual(Password, user.PasswordHash);
        Assert.Equal(PasswordVerification.Success, passwords.Verify(user, Password));
        Assert.Equal(PasswordVerification.Failed, passwords.Verify(user, Password + "x"));
    }

    [Fact]
    public async Task RunAgain_ExitsWithCode3AndChangesNothing()
    {
        await BootstrapAsync("admin");
        var before = Assert.Single(await _cli.UsersAsync());

        var again = await _cli.RunAsync(["admin", "bootstrap", "--username", "admin", "--password-stdin"], Password);
        var other = await _cli.RunAsync(["admin", "bootstrap", "--username", "second", "--password-stdin"], Password);

        Assert.Equal(3, again.ExitCode);
        Assert.Equal(3, other.ExitCode);
        Assert.Contains("An administrator already exists", other.Error, StringComparison.Ordinal);
        Assert.Empty(other.Out);
        var after = Assert.Single(await _cli.UsersAsync());
        Assert.Equal(before.ConcurrencyStamp, after.ConcurrencyStamp);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task WhenTheOnlyAdministratorIsDisabled_ExitsWithCode3AndChangesNothing()
    {
        await BootstrapAsync("admin");
        await using (var context = _database.CreateContext())
        {
            var admin = context.Users.Single();
            admin.IsActive = false;
            await context.SaveChangesAsync();
        }

        var run = await _cli.RunAsync(["admin", "bootstrap", "--username", "rescue", "--password-stdin"], Password);

        Assert.Equal(3, run.ExitCode);
        var user = Assert.Single(await _cli.UsersAsync());
        Assert.False(user.IsActive);
    }

    [Fact]
    public async Task WhenAnAdministratorWithoutOtherPermissionsExists_ExitsWithCode3()
    {
        await using (var context = await _database.CreateMigratedContextAsync())
        {
            var adminOnly = AuthDatabaseFixture.NewUser("ops");
            adminOnly.Permissions = Permission.Admin;
            context.Users.Add(adminOnly);
            await context.SaveChangesAsync();
        }

        var run = await _cli.RunAsync(["admin", "bootstrap", "--username", "admin", "--password-stdin"], Password);

        Assert.Equal(3, run.ExitCode);
        Assert.Single(await _cli.UsersAsync());
    }

    [Fact]
    public async Task WhenOnlyUsersWithoutAdminExist_Succeeds()
    {
        await using (var context = await _database.CreateMigratedContextAsync())
        {
            var publisher = AuthDatabaseFixture.NewUser("ci");
            publisher.Permissions = Permission.Read | Permission.Publish;
            context.Users.Add(publisher);
            await context.SaveChangesAsync();
        }

        var run = await _cli.RunAsync(["admin", "bootstrap", "--username", "admin", "--password-stdin"], Password);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(2, (await _cli.UsersAsync()).Count);
    }

    [Fact]
    public async Task WithTheNameOfAnExistingUserInAnotherCase_ExitsWithCode3()
    {
        await using (var context = await _database.CreateMigratedContextAsync())
        {
            context.Users.Add(AuthDatabaseFixture.NewUser("Alice"));
            await context.SaveChangesAsync();
        }

        var run = await _cli.RunAsync(["admin", "bootstrap", "--username", "alice", "--password-stdin"], Password);

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("already exists", run.Error, StringComparison.Ordinal);
        var user = Assert.Single(await _cli.UsersAsync());
        Assert.Equal(Permission.Read, user.Permissions);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("")]
    public async Task PasswordFile_WithOrWithoutATrailingNewline_IsUsedWithoutTheNewline(string ending)
    {
        var file = Path.Combine(_database.Directory, "admin-password");
        await File.WriteAllTextAsync(file, Password + ending);

        var run = await _cli.RunAsync("admin", "bootstrap", "--username", "admin", "--password-file", file);

        Assert.True(run.ExitCode == 0, run.ToString());
        var user = Assert.Single(await _cli.UsersAsync());
        Assert.Equal(PasswordVerification.Success, _cli.Services.GetRequiredService<IPasswordService>().Verify(user, Password));
    }

    [Fact]
    public async Task PasswordFile_WithTheEqualsSyntax_IsAccepted()
    {
        var file = Path.Combine(_database.Directory, "admin-password");
        await File.WriteAllTextAsync(file, Password);

        var run = await _cli.RunAsync("admin", "bootstrap", "--username=admin", $"--password-file={file}");

        Assert.True(run.ExitCode == 0, run.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("first line\nsecond line of a long password\n")]
    public async Task PasswordFile_MissingEmptyOrMultiLine_ExitsWithCode2AndCreatesNothing(string? content)
    {
        var file = Path.Combine(_database.Directory, "admin-password");
        if (content is not null)
        {
            await File.WriteAllTextAsync(file, content);
        }

        var run = await _cli.RunAsync("admin", "bootstrap", "--username", "admin", "--password-file", file);

        Assert.Equal(2, run.ExitCode);
        Assert.Empty(run.Out);
        Assert.False(File.Exists(_database.DatabasePath), "A usage error must not create the database.");
    }

    [Fact]
    public async Task PasswordFile_ThatIsADirectoryOrTooLarge_ExitsWithCode2()
    {
        var large = Path.Combine(_database.Directory, "large");
        await File.WriteAllTextAsync(large, new string('x', 20_000));

        var directory = await _cli.RunAsync("admin", "bootstrap", "--username", "admin", "--password-file", _database.Directory);
        var tooLarge = await _cli.RunAsync("admin", "bootstrap", "--username", "admin", "--password-file", large);

        Assert.Equal(2, directory.ExitCode);
        Assert.Contains("is a directory", directory.Error, StringComparison.Ordinal);
        Assert.Equal(2, tooLarge.ExitCode);
        Assert.Contains("larger than", tooLarge.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasswordFile_ThatIsNotUtf8_ExitsWithCode2()
    {
        var file = Path.Combine(_database.Directory, "latin1");
        await File.WriteAllBytesAsync(file, [0x70, 0xE9, 0xE9, 0xE9, 0xE9, 0xE9, 0xE9, 0xE9, 0xE9, 0xE9, 0xE9, 0xE9, 0xE9, 0xE9, 0xE9, 0xE9, 0xE9]);

        var run = await _cli.RunAsync("admin", "bootstrap", "--username", "admin", "--password-file", file);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("UTF-8", run.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--password", "s3cret-value-on-the-command-line")]
    [InlineData("--password=s3cret-value-on-the-command-line")]
    [InlineData("-p", "s3cret-value-on-the-command-line")]
    public async Task APasswordOption_IsRejectedWithCode2AndTheValueIsNotEchoed(params string[] passwordArguments)
    {
        var run = await _cli.RunAsync(["admin", "bootstrap", "--username", "admin", .. passwordArguments]);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("--password-file", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret-value-on-the-command-line", run.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(_cli.Logs, line => line.Contains("s3cret-value-on-the-command-line", StringComparison.Ordinal));
        Assert.False(File.Exists(_database.DatabasePath));
    }

    [Fact]
    public async Task APasswordGivenAsThePasswordFilePath_IsNotEchoed()
    {
        var run = await _cli.RunAsync("admin", "bootstrap", "--username", "admin", "--password-file", "s3cret-value-misplaced");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("does not exist", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret-value-misplaced", run.AllOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("admin", "bootstrap", "--username", "admin", "s3cret-value-misplaced")]
    [InlineData("admin", "bootstrap", "s3cret-value-misplaced", "--username", "admin")]
    [InlineData("admin", "bootstrap", "--username", "admin", "-s3cret-value-misplaced")]
    [InlineData("admin", "bootstrap", "--username", "admin", "--s3cret-value-misplaced=x")]
    [InlineData("admin", "s3cret-value-misplaced")]
    [InlineData("admin", "--s3cret-value-misplaced")]
    public async Task UnexpectedArguments_AreRejectedWithCode2WithoutBeingEchoed(params string[] arguments)
    {
        var run = await _cli.RunAsync(arguments);

        Assert.Equal(2, run.ExitCode);
        Assert.DoesNotContain("s3cret-value-misplaced", run.AllOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(_database.DatabasePath));
    }

    [Theory]
    [InlineData("--frobnicate")]
    [InlineData("--frobnicate=1")]
    public async Task AnUnknownOption_IsReportedByPositionWithTheValidOptions(string option)
    {
        var run = await _cli.RunAsync("admin", "bootstrap", "--username", "admin", option);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("Unknown option at position 3 after the command name. Valid options: --username, --password-file, --password-stdin.", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("frobnicate", run.AllOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("admin", "bootstrap", "--password-stdin")]
    [InlineData("admin", "bootstrap", "--username")]
    [InlineData("admin", "bootstrap", "--username", "--password-stdin")]
    [InlineData("admin", "bootstrap", "--username=", "--password-stdin")]
    [InlineData("admin", "bootstrap", "--username", "a", "--username", "b", "--password-stdin")]
    [InlineData("admin", "bootstrap", "--username", "admin", "--password-stdin=yes")]
    [InlineData("admin", "bootstrap", "--username", "admin", "--password-stdin", "--password-file", "x")]
    [InlineData("admin")]
    public async Task UsageErrors_ExitWithCode2(params string[] arguments)
    {
        var run = await _cli.RunAsync(arguments, Password);

        Assert.Equal(2, run.ExitCode);
        Assert.False(File.Exists(_database.DatabasePath));
    }

    [Fact]
    public async Task WithoutATerminalAndWithoutAPasswordOption_ExitsWithCode2InsteadOfBlocking()
    {
        var run = await _cli.RunAsync(["admin", "bootstrap", "--username", "admin"], stdin: Password, terminal: false);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("standard input is not a terminal", run.Error, StringComparison.Ordinal);
        Assert.Empty(_cli.Prompt.Prompts);
        Assert.False(File.Exists(_database.DatabasePath));
    }

    [Fact]
    public async Task InATerminal_PromptsTwiceOnTheErrorStreamAndCreatesTheAdministrator()
    {
        _cli.Prompt.Answer(Password, Password);

        var run = await _cli.RunAsync(["admin", "bootstrap", "--username", "admin"], terminal: true);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(2, _cli.Prompt.Prompts.Count);
        Assert.Contains("Password: ", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Password: ", run.Out, StringComparison.Ordinal);
        var user = Assert.Single(await _cli.UsersAsync());
        Assert.Equal(PasswordVerification.Success, _cli.Services.GetRequiredService<IPasswordService>().Verify(user, Password));
    }

    [Theory]
    [InlineData(Password, "a different passphrase")]
    [InlineData("", "")]
    public async Task InATerminal_AMismatchOrAnEmptyPassword_ExitsWithCode2(string first, string second)
    {
        _cli.Prompt.Answer(first, second);

        var run = await _cli.RunAsync(["admin", "bootstrap", "--username", "admin"], terminal: true);

        Assert.Equal(2, run.ExitCode);
        Assert.False(File.Exists(_database.DatabasePath));
    }

    [Fact]
    public async Task InATerminal_AnExplicitSourceWinsOverThePrompt()
    {
        var run = await _cli.RunAsync(["admin", "bootstrap", "--username", "admin", "--password-stdin"], Password, terminal: true);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Empty(_cli.Prompt.Prompts);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("admin")]
    [InlineData("ADMIN")]
    [InlineData("")]
    public async Task APasswordRefusedByThePolicy_ExitsWithCode2AndCreatesNothing(string password)
    {
        var run = await _cli.RunAsync(["admin", "bootstrap", "--username", "admin", "--password-stdin"], password + "\n");

        Assert.Equal(2, run.ExitCode);
        Assert.False(File.Exists(_database.DatabasePath), "A refused password must not create anything.");
    }

    [Fact]
    public async Task APasswordAtTheMaximumLength_IsAcceptedAndALongerOneRefused()
    {
        var atLimit = new string('p', 256);
        var tooLong = new string('p', 257);
        var muchTooLong = new string('p', 5_000);

        var refused = await _cli.RunAsync(["admin", "bootstrap", "--username", "admin", "--password-stdin"], tooLong);
        var refusedHuge = await _cli.RunAsync(["admin", "bootstrap", "--username", "admin", "--password-stdin"], muchTooLong);
        var accepted = await _cli.RunAsync(["admin", "bootstrap", "--username", "admin", "--password-stdin"], atLimit);

        Assert.Equal(2, refused.ExitCode);
        Assert.Equal(2, refusedHuge.ExitCode);
        Assert.True(accepted.ExitCode == 0, accepted.ToString());
    }

    [Theory]
    [InlineData("a")]
    [InlineData("Build.Agent_01-x")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task ValidUserNames_AreAccepted(string userName)
    {
        var run = await _cli.RunAsync(["admin", "bootstrap", "--username", userName, "--password-stdin"], Password);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(userName, Assert.Single(await _cli.UsersAsync()).UserName);
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("john doe")]
    [InlineData("john:doe")]
    [InlineData("jöhn")]
    [InlineData("jоhn")] // Cyrillic o
    [InlineData("john\tdoe")]
    [InlineData("john@example.test")]
    [InlineData("admin\n")]
    [InlineData("admin\r\n")]
    [InlineData("\nadmin")]
    public async Task InvalidUserNames_ExitWithCode2AndCreateNothing(string userName)
    {
        var run = await _cli.RunAsync(["admin", "bootstrap", "--username", userName, "--password-stdin"], Password);

        Assert.Equal(2, run.ExitCode);
        Assert.False(File.Exists(_database.DatabasePath));
    }

    [Fact]
    public async Task ThePasswordAndItsHash_NeverAppearInAnyOutputOrLog()
    {
        const string sentinel = "s3ntinel-bootstrap-password-0123456789";
        var file = Path.Combine(_database.Directory, "secret");
        await File.WriteAllTextAsync(file, sentinel + "\n");

        var runs = new List<CliRun>
        {
            await _cli.RunAsync(["admin", "bootstrap", "--username", "admin", "--password-stdin"], sentinel),
            await _cli.RunAsync(["admin", "bootstrap", "--username", "admin", "--password-stdin"], sentinel),
            await _cli.RunAsync("admin", "bootstrap", "--username", "other", "--password-file", file),
            await _cli.RunAsync(["admin", "bootstrap", "--username", sentinel, "--password-stdin"], sentinel),
            await _cli.RunAsync(["admin", "bootstrap", "--username", "x", "--password-stdin"], sentinel[..10]),
            await _cli.RunAsync(["admin", "bootstrap", "--username", "admin", "--password", sentinel]),
        };
        var hash = Assert.Single(await _cli.UsersAsync()).PasswordHash;

        Assert.Equal([0, 3, 3, 2, 2, 2], runs.Select(r => r.ExitCode));
        foreach (var run in runs)
        {
            Assert.DoesNotContain(sentinel, run.AllOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(hash, run.AllOutput, StringComparison.Ordinal);
        }

        Assert.NotEmpty(_cli.Logs);
        Assert.DoesNotContain(_cli.Logs, line => line.Contains(sentinel, StringComparison.Ordinal));
        Assert.DoesNotContain(_cli.Logs, line => line.Contains(hash, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Success_IsLoggedAsAStructuredEventWithoutSecrets()
    {
        await BootstrapAsync("admin");

        var line = Assert.Single(_cli.Logs, l => l.StartsWith("2001 ", StringComparison.Ordinal));
        Assert.Contains("UserName=admin", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoConcurrentBootstraps_ExactlyOneSucceeds()
    {
        // Widens the race: every check for an existing administrator stalls before the write. Without a
        // write transaction taken before that check, both commands would pass it and create two administrators.
        var slowCheck = new SlowAdministratorCheck();
        using var cli = new AdminCli(_database, slowCheck);
        await using (var context = await _database.CreateMigratedContextAsync())
        {
            // Migrated up front: the concurrency under test is the bootstrap, not the migration.
        }

        using var start = new ManualResetEventSlim();
        var first = Task.Run(() =>
        {
            start.Wait();
            return cli.RunAsync(["admin", "bootstrap", "--username", "first", "--password-stdin"], Password);
        });
        var second = Task.Run(() =>
        {
            start.Wait();
            return cli.RunAsync(["admin", "bootstrap", "--username", "second", "--password-stdin"], Password);
        });
        start.Set();
        var runs = await Task.WhenAll(first, second);

        Assert.True(
            runs.Count(r => r.ExitCode == 0) == 1 && runs.Count(r => r.ExitCode == 3) == 1,
            string.Join(Environment.NewLine, runs.Select(r => r.ToString())));
        var admin = Assert.Single(await cli.UsersAsync());
        Assert.Equal(Permission.Read | Permission.Publish | Permission.Admin, admin.Permissions);
        Assert.Equal(2, slowCheck.Stalls);
    }

    [Fact]
    public async Task Help_DocumentsThePasswordSourcesAndExitsWithCode0()
    {
        var run = await _cli.RunAsync("admin", "bootstrap", "--help");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("--password-file <path>", run.Out, StringComparison.Ordinal);
        Assert.Contains("--password-stdin", run.Out, StringComparison.Ordinal);
        Assert.Contains("There is no --password option", run.Out, StringComparison.Ordinal);
        Assert.False(File.Exists(_database.DatabasePath));
    }

    private async Task BootstrapAsync(string userName)
    {
        var run = await _cli.RunAsync(["admin", "bootstrap", "--username", userName, "--password-stdin"], Password);
        Assert.True(run.ExitCode == 0, run.ToString());
    }

    /// <summary>Stalls after reading whether an administrator exists, to widen the race window.</summary>
    private sealed class SlowAdministratorCheck : DbCommandInterceptor
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
            if (command.CommandText.Contains("\"Permissions\" & 4", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _stalls);
                Thread.Sleep(TimeSpan.FromMilliseconds(500));
            }
        }
    }
}
