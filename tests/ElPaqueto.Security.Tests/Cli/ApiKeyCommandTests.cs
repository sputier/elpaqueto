using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Cli;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ElPaqueto.Security.Tests.Cli;

/// <summary>
/// <c>admin key …</c> run in process over a real SQLite security database: output streams, exit codes,
/// validation of the created keys with <see cref="IApiKeyService"/>, and that the token is never written
/// anywhere but once to standard output.
/// </summary>
public sealed partial class ApiKeyCommandTests : IDisposable
{
    private const string Password = "a long enough passphrase";

    private readonly AuthDatabaseFixture _database = new();
    private readonly AdminCli _cli;

    public ApiKeyCommandTests()
    {
        _cli = new AdminCli(_database);
    }

    public void Dispose()
    {
        _cli.Dispose();
        _database.Dispose();
    }

    // ---- create ----

    [Fact]
    public async Task Create_WritesExactlyTheTokenToStandardOutput_AndEverythingElseToStandardError()
    {
        await UserAsync("ci", "publisher");

        var run = await _cli.RunAsync("admin", "key", "create", "--user", "ci", "--name", "ci-main", "--permissions", "read,publish", "--expires", "90d");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Matches(TokenLine(), run.Out);
        var token = run.Out[..^1];
        Assert.DoesNotContain(token, run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret(token), run.Error, StringComparison.Ordinal);
        Assert.Contains(KeyId(token), run.Error, StringComparison.Ordinal);
        Assert.Contains("shown only once", run.Error, StringComparison.Ordinal);
        Assert.Contains("read, publish", run.Error, StringComparison.Ordinal);

        var validation = await ValidateAsync(token);
        Assert.True(validation.Succeeded, validation.Failure.ToString());
        Assert.Equal(Permission.Read | Permission.Publish, validation.EffectivePermissions);
        Assert.Equal("ci", validation.OwnerUserName);

        var key = Assert.Single(await KeysAsync());
        Assert.Equal("ci-main", key.Name);
        Assert.Equal(_cli.Clock.UtcNow.AddDays(90), key.ExpiresAt);
        Assert.Contains(_cli.Logs, l => l.StartsWith("2010 ", StringComparison.Ordinal) && l.Contains(KeyId(token), StringComparison.Ordinal));
        AssertNeverLogged(token);
    }

    [Theory]
    [InlineData("read", Permission.Read)]
    [InlineData("publish", Permission.Publish)]
    [InlineData("reader", Permission.Read)]
    [InlineData("Read,Publish", Permission.Read | Permission.Publish)]
    [InlineData("administrator", Permission.Read | Permission.Publish | Permission.Admin)]
    [InlineData("admin", Permission.Admin)]
    public async Task Create_GrantsExactlyTheRequestedPermissions(string permissions, Permission expected)
    {
        await UserAsync("root", "administrator");

        var token = await CreateKeyAsync("root", permissions);

        Assert.Equal(expected, (await ValidateAsync(token)).EffectivePermissions);
    }

    [Fact]
    public async Task Create_AsJson_WritesOneObjectWithTheTokenAndNothingElse()
    {
        await UserAsync("ci", "publisher");

        var run = await _cli.RunAsync("admin", "key", "create", "--user", "ci", "--name", "ci", "--permissions", "read", "--no-expiry", "--format", "json");

        Assert.True(run.ExitCode == 0, run.ToString());
        using var document = JsonDocument.Parse(run.Out);
        var token = document.RootElement.GetProperty("token").GetString()!;
        Assert.Matches(ApiKeyToken.Pattern, token);
        var key = document.RootElement.GetProperty("key");
        Assert.Equal(KeyId(token), key.GetProperty("keyId").GetString());
        Assert.Equal("ci", key.GetProperty("owner").GetString());
        Assert.Equal("active", key.GetProperty("status").GetString());
        Assert.Equal(["read"], key.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(JsonValueKind.Null, key.GetProperty("expiresAt").ValueKind);
        Assert.False(document.RootElement.TryGetProperty("replacedKeyId", out _));
        Assert.DoesNotContain(token, run.Error, StringComparison.Ordinal);
        Assert.True((await ValidateAsync(token)).Succeeded);
    }

    [Theory]
    [InlineData("2026-03-01", "2026-03-01T00:00:00Z")]
    [InlineData("2026-03-01T12:30:00Z", "2026-03-01T12:30:00Z")]
    [InlineData("2026-03-01T12:30Z", "2026-03-01T12:30:00Z")]
    [InlineData("2026-03-01T14:30:00+02:00", "2026-03-01T12:30:00Z")]
    [InlineData("1d", "2026-01-03T03:04:05Z")]
    public async Task Create_AcceptsTheExpiryForms(string expires, string expected)
    {
        await UserAsync("ci", "reader");

        var run = await _cli.RunAsync("admin", "key", "create", "--user", "ci", "--name", "k", "--permissions", "read", "--expires", expires);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime, Assert.Single(await KeysAsync()).ExpiresAt);
    }

    [Fact]
    public async Task Create_AcceptsTheLongestRelativeExpiry()
    {
        await UserAsync("ci", "reader");

        var run = await _cli.RunAsync("admin", "key", "create", "--user", "ci", "--name", "k", "--permissions", "read", "--expires", "36500D");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(_cli.Clock.UtcNow.AddDays(ExpiryText.MaximumDays), Assert.Single(await KeysAsync()).ExpiresAt);
    }

    [Theory]
    [InlineData("--name", "k", "--permissions", "read", "--expires", "90d")]
    [InlineData("--user", "ci", "--permissions", "read", "--expires", "90d")]
    [InlineData("--user", "ci", "--name", "k", "--expires", "90d")]
    [InlineData("--user", "ci", "--name", "k", "--permissions", "read")]
    [InlineData("--user", "ci", "--name", "k", "--permissions", "read", "--expires", "90d", "--no-expiry")]
    [InlineData("--user", "ci", "--name", "k", "--permissions", "write", "--expires", "90d")]
    [InlineData("--user", "ci", "--name", "k", "--permissions", "read", "--expires", "90d", "--format", "xml")]
    [InlineData("--user", "c i", "--name", "k", "--permissions", "read", "--expires", "90d")]
    [InlineData("--user", "ci", "--name", " ", "--permissions", "read", "--expires", "90d")]
    [InlineData("--user", "ci", "--name", "a\tb", "--permissions", "read", "--expires", "90d")]
    public async Task Create_WithMissingOrInvalidArguments_ExitsWithCode2BeforeTouchingTheDatabase(params string[] options)
    {
        var run = await _cli.RunAsync(["admin", "key", "create", .. options]);

        Assert.Equal(2, run.ExitCode);
        Assert.Empty(run.Out);
        Assert.False(File.Exists(_database.DatabasePath));
    }

    [Theory]
    [InlineData("0d")]
    [InlineData("36501d")]
    [InlineData("-5d")]
    [InlineData("90")]
    [InlineData("90 d")]
    [InlineData("3m")]
    [InlineData("2026-02-30")]
    [InlineData("2026-03-01T12:00:00")]
    [InlineData("01/03/2026")]
    [InlineData("2026-01-01")]
    [InlineData("2026-01-02T03:04:05Z")]
    [InlineData("2020-01-01T00:00:00Z")]
    public async Task Create_WithAnInvalidOrPastExpiry_ExitsWithCode2(string expires)
    {
        await UserAsync("ci", "reader");

        var run = await _cli.RunAsync("admin", "key", "create", "--user", "ci", "--name", "k", "--permissions", "read", "--expires", expires);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("--expires", run.Error, StringComparison.Ordinal);
        Assert.Empty(run.Out);
        Assert.Empty(await KeysAsync());
    }

    [Theory]
    [InlineData("list")]
    [InlineData("list", "--user", "c i")]
    [InlineData("list", "--format", "xml")]
    [InlineData("show", "short")]
    [InlineData("show", "AAAAAAAAAAAA", "--format", "xml")]
    [InlineData("revoke", "AAAAAAAAAAA!")]
    [InlineData("rotate", "AAAAAAAAAAAA")]
    [InlineData("rotate", "AAAAAAAAAAAA", "--expires", "2020-01-01")]
    [InlineData("rotate")]
    public async Task OtherCommands_WithInvalidArguments_ExitWithCode2BeforeTouchingTheDatabase(string command, params string[] rest)
    {
        // "list" alone is valid: it is the control that the database is created by a valid command.
        var run = await _cli.RunAsync(["admin", "key", command, .. rest]);

        if (command == "list" && rest.Length == 0)
        {
            Assert.Equal(0, run.ExitCode);
            Assert.True(File.Exists(_database.DatabasePath));
            return;
        }

        Assert.Equal(2, run.ExitCode);
        Assert.False(File.Exists(_database.DatabasePath));
    }

    [Theory]
    [InlineData("--user")]
    [InlineData("--permissions")]
    [InlineData("--name")]
    public async Task AKeyGivenInAnotherOption_IsNeverRepeated(string option)
    {
        await UserAsync("ci", "publisher");
        var token = await CreateKeyAsync("ci", "read");
        var options = new Dictionary<string, string> { ["--user"] = "ci", ["--name"] = "k", ["--permissions"] = "read" };
        options[option] = token;
        string[] arguments = ["admin", "key", "create", .. options.SelectMany(o => new[] { o.Key, o.Value }), "--expires", "1d"];

        var create = await _cli.RunAsync(arguments);
        var list = await _cli.RunAsync("admin", "key", "list", "--user", token);

        Assert.NotEqual(0, create.ExitCode);
        Assert.DoesNotContain(Secret(token), create.AllOutput, StringComparison.Ordinal);
        Assert.NotEqual(0, list.ExitCode);
        Assert.DoesNotContain(Secret(token), list.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ForAnUnknownUser_ExitsWithCode4()
    {
        await UserAsync("ci", "reader");

        var run = await _cli.RunAsync("admin", "key", "create", "--user", "ghost", "--name", "k", "--permissions", "read", "--expires", "1d");

        Assert.Equal(4, run.ExitCode);
        Assert.Contains("'ghost' does not exist", run.Error, StringComparison.Ordinal);
        Assert.Empty(run.Out);
        Assert.Empty(await KeysAsync());
    }

    [Fact]
    public async Task Create_ForADisabledUser_ExitsWithCode3()
    {
        await UserAsync("admin", "administrator");
        await UserAsync("ci", "publisher");
        Assert.Equal(0, (await _cli.RunAsync("admin", "user", "disable", "ci")).ExitCode);

        var run = await _cli.RunAsync("admin", "key", "create", "--user", "ci", "--name", "k", "--permissions", "read", "--expires", "1d");

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("disabled", run.Error, StringComparison.Ordinal);
        Assert.Empty(run.Out);
        Assert.Empty(await KeysAsync());
    }

    [Theory]
    [InlineData("reader", "publish", "publish")]
    [InlineData("reader", "read,publish", "publish")]
    [InlineData("publisher", "admin", "admin")]
    [InlineData("publisher", "administrator", "admin")]
    [InlineData("admin", "read", "read")]
    public async Task Create_WithAPermissionTheOwnerDoesNotHold_ExitsWithCode3(string ownerPermissions, string keyPermissions, string missing)
    {
        await UserAsync("owner", ownerPermissions);

        var run = await _cli.RunAsync("admin", "key", "create", "--user", "owner", "--name", "k", "--permissions", keyPermissions, "--expires", "1d");

        Assert.Equal(3, run.ExitCode);
        Assert.Contains($"does not hold these permissions: {missing}.", run.Error, StringComparison.Ordinal);
        Assert.Empty(run.Out);
        Assert.Empty(await KeysAsync());
    }

    [Fact]
    public async Task Create_MatchesTheUserIgnoringCase()
    {
        await UserAsync("Alice", "reader");

        var token = await CreateKeyAsync("ALICE", "read");

        Assert.Equal("Alice", (await ValidateAsync(token)).OwnerUserName);
    }

    // ---- list / show ----

    [Fact]
    public async Task List_ShowsKeysWithStatusAndEffectivePermissions_HidingRevokedOnesUnlessAsked()
    {
        await UserAsync("admin", "administrator");
        await UserAsync("ci", "publisher");
        await UserAsync("bob", "reader");
        var active = await CreateKeyAsync("ci", "read,publish", "--no-expiry");
        var revoked = await CreateKeyAsync("ci", "read");
        var expiring = await CreateKeyAsync("bob", "read", "--expires", "2026-01-03T00:00:00Z");
        Assert.Equal(0, (await _cli.RunAsync("admin", "key", "revoke", KeyId(revoked))).ExitCode);
        Assert.Equal(0, (await _cli.RunAsync("admin", "user", "set-permissions", "ci", "--permissions", "read")).ExitCode);
        _cli.Clock.Advance(TimeSpan.FromDays(1));

        var list = await JsonAsync("admin", "key", "list", "--format", "json");
        var all = await JsonAsync("admin", "key", "list", "--include-revoked", "--format", "json");
        var ofBob = await JsonAsync("admin", "key", "list", "--user", "BOB", "--format", "json");

        Assert.Equal([KeyId(active), KeyId(expiring)], KeyIds(list));
        Assert.Equal([KeyId(active), KeyId(revoked), KeyId(expiring)], KeyIds(all));
        Assert.Equal([KeyId(expiring)], KeyIds(ofBob));

        var keys = all.GetProperty("keys").EnumerateArray().ToDictionary(k => k.GetProperty("keyId").GetString()!);
        var first = keys[KeyId(active)];
        Assert.Equal("active", first.GetProperty("status").GetString());
        Assert.Equal("ci", first.GetProperty("owner").GetString());
        Assert.Equal(["read", "publish"], first.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["read"], first.GetProperty("effectivePermissions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("revoked", keys[KeyId(revoked)].GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, keys[KeyId(revoked)].GetProperty("revokedAt").ValueKind);
        Assert.Equal("expired", keys[KeyId(expiring)].GetProperty("status").GetString());

        var text = await _cli.RunAsync("admin", "key", "list", "--include-revoked");
        Assert.Equal(0, text.ExitCode);
        Assert.Contains(KeyId(active), text.Out, StringComparison.Ordinal);
        Assert.Contains("owner-disabled", (await DisableAndListAsync("ci")).Out, StringComparison.Ordinal);

        foreach (var token in new[] { active, revoked, expiring })
        {
            Assert.DoesNotContain(Secret(token), text.Out + all.ToString(), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("hash", all.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_WithoutKeys_SaysSo_AndForAnUnknownUserExitsWithCode4()
    {
        await UserAsync("ci", "reader");

        var empty = await _cli.RunAsync("admin", "key", "list");
        var unknown = await _cli.RunAsync("admin", "key", "list", "--user", "ghost");

        Assert.Equal(0, empty.ExitCode);
        Assert.Equal("No API keys." + Environment.NewLine, empty.Out);
        Assert.Equal(4, unknown.ExitCode);
    }

    [Fact]
    public async Task Show_DisplaysOneKeyAsTextOrJsonWithoutItsSecret()
    {
        await UserAsync("ci", "publisher");
        var token = await CreateKeyAsync("ci", "publish", "--expires", "2026-06-30");

        var text = await _cli.RunAsync("admin", "key", "show", KeyId(token));
        var json = await JsonAsync("admin", "key", "show", KeyId(token), "--format", "json");

        Assert.Equal(0, text.ExitCode);
        Assert.Contains($"Key id:                {KeyId(token)}", text.Out, StringComparison.Ordinal);
        Assert.Contains("Expires:               2026-06-30 00:00:00 UTC", text.Out, StringComparison.Ordinal);
        Assert.Contains("Effective permissions: publish", text.Out, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret(token), text.AllOutput, StringComparison.Ordinal);
        Assert.Equal(KeyId(token), json.GetProperty("keyId").GetString());
        Assert.Equal("2026-06-30T00:00:00.0000000Z", json.GetProperty("expiresAt").GetString());
        Assert.DoesNotContain(Secret(token), json.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_Revoke_Rotate_OfAnUnknownKeyId_ExitWithCode4()
    {
        await UserAsync("ci", "reader");

        foreach (var command in new[] { "show", "revoke" })
        {
            var run = await _cli.RunAsync("admin", "key", command, "AAAAAAAAAAAA");
            Assert.Equal(4, run.ExitCode);
            Assert.Contains("AAAAAAAAAAAA does not exist", run.Error, StringComparison.Ordinal);
        }

        Assert.Equal(4, (await _cli.RunAsync("admin", "key", "rotate", "AAAAAAAAAAAA", "--expires", "1d")).ExitCode);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("revoke")]
    [InlineData("rotate")]
    public async Task AKeyIdArgument_ThatIsACompleteKey_IsRefusedWithoutRepeatingIt(string command)
    {
        await UserAsync("ci", "reader");
        var token = await CreateKeyAsync("ci", "read");

        string[] expiry = command == "rotate" ? ["--expires", "1d"] : [];
        var run = await _cli.RunAsync(["admin", "key", command, token, .. expiry]);
        var malformed = await _cli.RunAsync(["admin", "key", command, "short", .. expiry]);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("not the complete key", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret(token), run.AllOutput, StringComparison.Ordinal);
        Assert.Equal(2, malformed.ExitCode);
        Assert.True((await ValidateAsync(token)).Succeeded, "The key must not have been revoked or rotated.");
    }

    // ---- revoke ----

    [Fact]
    public async Task CreatedKey_Validates_ThenAfterRevoke_IsRefused_AndASecondRevokeIsANoOp()
    {
        await UserAsync("ci", "publisher");
        var token = await CreateKeyAsync("ci", "read,publish");
        Assert.True((await ValidateAsync(token)).Succeeded);

        var revoke = await _cli.RunAsync("admin", "key", "revoke", KeyId(token));
        var again = await _cli.RunAsync("admin", "key", "revoke", KeyId(token));

        Assert.True(revoke.ExitCode == 0, revoke.ToString());
        Assert.Equal($"Revoked API key {KeyId(token)}.{Environment.NewLine}", revoke.Out);
        var refused = await ValidateAsync(token);
        Assert.False(refused.Succeeded);
        Assert.Equal(ApiKeyValidationFailure.Revoked, refused.Failure);
        Assert.Equal(0, again.ExitCode);
        Assert.Contains("already revoked", again.Error, StringComparison.Ordinal);
        Assert.Empty(again.Out);
        Assert.Contains(_cli.Logs, l => l.StartsWith("2011 ", StringComparison.Ordinal) && l.Contains(KeyId(token), StringComparison.Ordinal));
    }

    // ---- rotate ----

    [Fact]
    public async Task Rotate_PrintsANewTokenThatValidates_AndTheOldOneStopsWorking()
    {
        await UserAsync("ci", "publisher");
        var old = await CreateKeyAsync("ci", "read,publish", "--expires", "30d");

        var run = await _cli.RunAsync("admin", "key", "rotate", KeyId(old), "--expires", "60d");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Matches(TokenLine(), run.Out);
        var token = run.Out[..^1];
        Assert.NotEqual(old, token);
        Assert.DoesNotContain(token, run.Error, StringComparison.Ordinal);
        Assert.Contains("shown only once", run.Error, StringComparison.Ordinal);
        Assert.Equal(ApiKeyValidationFailure.Revoked, (await ValidateAsync(old)).Failure);
        var validation = await ValidateAsync(token);
        Assert.True(validation.Succeeded);
        Assert.Equal(Permission.Read | Permission.Publish, validation.EffectivePermissions);

        var keys = await KeysAsync();
        var replacement = keys.Single(k => k.KeyId == KeyId(token));
        Assert.Equal(keys.Single(k => k.KeyId == KeyId(old)).Name, replacement.Name);
        Assert.Equal(_cli.Clock.UtcNow.AddDays(60), replacement.ExpiresAt);
        Assert.Contains(_cli.Logs, l => l.StartsWith("2012 ", StringComparison.Ordinal) && l.Contains(KeyId(token), StringComparison.Ordinal));
        AssertNeverLogged(old);
        AssertNeverLogged(token);
    }

    [Fact]
    public async Task Rotate_AsJson_NamesTheReplacedKey()
    {
        await UserAsync("ci", "reader");
        var old = await CreateKeyAsync("ci", "read");

        var json = await JsonAsync("admin", "key", "rotate", KeyId(old), "--no-expiry", "--format", "json");

        Assert.Equal(KeyId(old), json.GetProperty("replacedKeyId").GetString());
        var token = json.GetProperty("token").GetString()!;
        Assert.Equal(KeyId(token), json.GetProperty("key").GetProperty("keyId").GetString());
        Assert.True((await ValidateAsync(token)).Succeeded);
    }

    [Fact]
    public async Task Rotate_WithoutAnExpiryChoice_ExitsWithCode2AndKeepsTheKey()
    {
        await UserAsync("ci", "reader");
        var token = await CreateKeyAsync("ci", "read");

        var run = await _cli.RunAsync("admin", "key", "rotate", KeyId(token));

        Assert.Equal(2, run.ExitCode);
        Assert.Empty(run.Out);
        Assert.True((await ValidateAsync(token)).Succeeded);
        Assert.Single(await KeysAsync());
    }

    [Fact]
    public async Task Rotate_OfARevokedKey_ExitsWithCode3()
    {
        await UserAsync("ci", "reader");
        var token = await CreateKeyAsync("ci", "read");
        await _cli.RunAsync("admin", "key", "revoke", KeyId(token));

        var run = await _cli.RunAsync("admin", "key", "rotate", KeyId(token), "--expires", "1d");

        Assert.Equal(3, run.ExitCode);
        Assert.Empty(run.Out);
        Assert.Single(await KeysAsync());
    }

    [Fact]
    public async Task Rotate_WhenTheOwnerLostAPermissionOfTheKey_ExitsWithCode3AndTheOldKeyStillWorks()
    {
        await UserAsync("admin", "administrator");
        await UserAsync("ci", "publisher");
        var token = await CreateKeyAsync("ci", "read,publish");
        Assert.Equal(0, (await _cli.RunAsync("admin", "user", "set-permissions", "ci", "--permissions", "read")).ExitCode);

        var run = await _cli.RunAsync("admin", "key", "rotate", KeyId(token), "--expires", "1d");

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("no longer holds these permissions of API key", run.Error, StringComparison.Ordinal);
        Assert.Empty(run.Out);
        var validation = await ValidateAsync(token);
        Assert.True(validation.Succeeded);
        Assert.Equal(Permission.Read, validation.EffectivePermissions);
    }

    [Fact]
    public async Task Rotate_WhenTheOwnerIsDisabled_ExitsWithCode3()
    {
        await UserAsync("admin", "administrator");
        await UserAsync("ci", "reader");
        var token = await CreateKeyAsync("ci", "read");
        Assert.Equal(0, (await _cli.RunAsync("admin", "user", "disable", "ci")).ExitCode);

        var run = await _cli.RunAsync("admin", "key", "rotate", KeyId(token), "--expires", "1d");

        Assert.Equal(3, run.ExitCode);
        Assert.Single(await KeysAsync());
    }

    // ---- the token is written once ----

    [Fact]
    public async Task TheToken_NeverReachesTheLogs_OtherCommands_OrTheDatabaseFile()
    {
        await UserAsync("ci", "publisher");
        var created = await CreateKeyAsync("ci", "read,publish");
        var rotated = (await _cli.RunAsync("admin", "key", "rotate", KeyId(created), "--expires", "1d")).Out[..^1];

        var others = new StringBuilder();
        foreach (var arguments in new[]
        {
            new[] { "admin", "key", "list", "--include-revoked" },
            ["admin", "key", "list", "--include-revoked", "--format", "json"],
            ["admin", "key", "show", KeyId(created)],
            ["admin", "key", "show", KeyId(rotated), "--format", "json"],
            ["admin", "key", "revoke", KeyId(rotated)],
        })
        {
            others.Append((await _cli.RunAsync(arguments)).AllOutput);
        }

        SqliteConnection.ClearAllPools();
        var files = Directory.GetFiles(Path.GetDirectoryName(_database.DatabasePath)!);
        var bytes = files.SelectMany(File.ReadAllBytes).ToArray();
        var ascii = Encoding.Latin1.GetString(bytes);
        var utf16 = Encoding.Unicode.GetString(bytes) + Encoding.Unicode.GetString(bytes.Skip(1).ToArray());

        foreach (var token in new[] { created, rotated })
        {
            AssertNeverLogged(token);
            Assert.DoesNotContain(Secret(token), others.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Secret(token), ascii, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret(token), utf16, StringComparison.Ordinal);
            Assert.Contains(KeyId(token), ascii, StringComparison.Ordinal);
        }

        Assert.Contains(_cli.Logs, l => l.StartsWith("2010 ", StringComparison.Ordinal));
        Assert.Contains(_cli.Logs, l => l.StartsWith("2012 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Help_ListsTheKeyCommands()
    {
        var run = await _cli.RunAsync("admin", "key", "--help");

        foreach (var command in new[] { "create", "list", "show", "revoke", "rotate" })
        {
            Assert.Contains(command, run.Out, StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(@"\Aepq_[0-9A-Za-z]{12}_[0-9A-Za-z]{43}\n\z")]
    private static partial Regex TokenLine();

    private static string KeyId(string token) => token.Substring(4, 12);

    private static string Secret(string token) => token[17..];

    private static string[] KeyIds(JsonElement list)
        => [.. list.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("keyId").GetString()!)];

    private async Task<CliRun> DisableAndListAsync(string userName)
    {
        Assert.Equal(0, (await _cli.RunAsync("admin", "user", "disable", userName)).ExitCode);
        return await _cli.RunAsync("admin", "key", "list", "--include-revoked");
    }

    private void AssertNeverLogged(string token)
    {
        foreach (var line in _cli.Logs)
        {
            Assert.DoesNotContain(Secret(token), line, StringComparison.Ordinal);
        }
    }

    private async Task UserAsync(string userName, string permissions)
    {
        var run = await _cli.RunAsync(["admin", "user", "create", userName, "--permissions", permissions, "--password-stdin"], Password);
        Assert.True(run.ExitCode == 0, run.ToString());
    }

    private async Task<string> CreateKeyAsync(string userName, string permissions, params string[] expiry)
    {
        // Keys created at the same instant are listed in key id order: a clock tick keeps the creation order.
        _cli.Clock.Advance(TimeSpan.FromMinutes(1));
        string[] arguments = ["admin", "key", "create", "--user", userName, "--name", "key-of-" + userName, "--permissions", permissions];
        var run = await _cli.RunAsync([.. arguments, .. expiry.Length == 0 ? ["--expires", "30d"] : expiry]);
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Matches(TokenLine(), run.Out);
        return run.Out[..^1];
    }

    private async Task<JsonElement> JsonAsync(params string[] arguments)
    {
        var run = await _cli.RunAsync(arguments);
        Assert.True(run.ExitCode == 0, run.ToString());
        using var document = JsonDocument.Parse(run.Out);
        return document.RootElement.Clone();
    }

    private async Task<ApiKeyValidationResult> ValidateAsync(string token)
    {
        using var scope = _cli.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IApiKeyService>().ValidateAsync(token);
    }

    private async Task<List<Data.ApiKey>> KeysAsync()
    {
        if (!File.Exists(_database.DatabasePath))
        {
            return [];
        }

        await using var context = _database.CreateContext();
        return await context.ApiKeys.AsNoTracking().OrderBy(k => k.CreatedAt).ToListAsync();
    }
}
