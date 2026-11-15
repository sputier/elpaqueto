using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace ElPaqueto.Security.Tests.Cli;

/// <summary>
/// <c>admin key …</c> run as real child processes of the built host (<c>ElPaqueto.dll</c>) against the
/// security database of a running server, then the keys used over HTTP on that server: the header
/// <c>X-NuGet-ApiKey</c> and the Basic password, revocation, rotation, the owner's permissions and expiry.
/// </summary>
/// <remarks>
/// <c>v3/index.json</c> needs <c>Read</c>, <c>health/details</c> needs <c>Admin</c>, and
/// <c>PUT api/v2/package</c> needs <c>Publish</c> sent in the header: 401 means refused credentials and
/// 403 a missing permission. Every test stays far below the throttling limits.
/// </remarks>
public sealed partial class ApiKeyCommandProcessTests : IDisposable
{
    private const string Password = "a long enough passphrase";

    private readonly AuthDatabaseFixture _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task CreatedKey_AuthenticatesAsHeaderAndAsBasicPassword_UntilRevoked()
    {
        using var server = await StartServerAsync();
        using var client = Client(server);
        await UsersAsync(("ci", "publisher"));

        var run = await RunAsync("admin", "key", "create", "--user", "ci", "--name", "ci-main", "--permissions", "read,publish", "--expires", "90d");
        var token = TokenOf(run);
        var readOnly = TokenOf(await RunAsync("admin", "key", "create", "--user", "ci", "--name", "restore", "--permissions", "read", "--no-expiry"));

        // Everything is logged at Information level, yet standard output holds the key alone: logs go to standard error.
        Assert.Contains("Executed DbCommand", run.Output, StringComparison.Ordinal);
        Assert.Equal(token + Environment.NewLine, run.StandardOutput);
        Assert.Single(Regex.Matches(run.Output, Regex.Escape(token)));

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, "v3/index.json", Header(token)));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, "v3/index.json", Basic(token)));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, "v3/index.json", Basic(readOnly)));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client, HttpMethod.Get, "health/details", Header(token)));

        // A write needs Publish in the header: the request passes authorization and fails on its empty body.
        var write = await StatusAsync(client, HttpMethod.Put, "api/v2/package", Header(token));
        Assert.NotEqual(HttpStatusCode.Unauthorized, write);
        Assert.NotEqual(HttpStatusCode.Forbidden, write);
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client, HttpMethod.Put, "api/v2/package", Basic(token)));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client, HttpMethod.Put, "api/v2/package", Header(readOnly)));

        var revoke = await RunAsync("admin", "key", "revoke", KeyId(token));
        Assert.Equal($"Revoked API key {KeyId(token)}.{Environment.NewLine}", revoke.StandardOutput);

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, HttpMethod.Get, "v3/index.json", Header(token)));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, HttpMethod.Get, "v3/index.json", Basic(token)));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, "v3/index.json", Header(readOnly)));

        Assert.False(server.HasExited, "The server stopped while commands used its security database.");
        Assert.DoesNotContain(Secret(token), server.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret(readOnly), server.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rotate_TheOldTokenIsRefusedAtOnce_AndTheNewOneWorks()
    {
        using var server = await StartServerAsync();
        using var client = Client(server);
        await UsersAsync(("ci", "publisher"));
        var old = TokenOf(await RunAsync("admin", "key", "create", "--user", "ci", "--name", "ci-main", "--permissions", "read", "--expires", "30d"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, "v3/index.json", Header(old)));

        var rotated = TokenOf(await RunAsync("admin", "key", "rotate", KeyId(old), "--expires", "30d"));

        Assert.NotEqual(old, rotated);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, HttpMethod.Get, "v3/index.json", Header(old)));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, "v3/index.json", Header(rotated)));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, "v3/index.json", Basic(rotated)));
    }

    [Fact]
    public async Task AKey_NeverGrantsMoreThanItsOwnerHolds()
    {
        using var server = await StartServerAsync();
        using var client = Client(server);
        await UsersAsync(("reader", "reader"), ("ops", "administrator"));

        // Refused at creation.
        var refused = await RunAsync(3, "admin", "key", "create", "--user", "reader", "--name", "k", "--permissions", "read,publish", "--expires", "1d");
        Assert.Empty(refused.StandardOutput);

        // Restricted at use when the owner loses a permission after the creation.
        var key = TokenOf(await RunAsync("admin", "key", "create", "--user", "ops", "--name", "ops", "--permissions", "read,admin", "--expires", "1d"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, "health/details", Header(key)));

        await RunAsync("admin", "user", "set-permissions", "ops", "--permissions", "read");

        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client, HttpMethod.Get, "health/details", Header(key)));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, "v3/index.json", Header(key)));

        // A key with only some of the owner's permissions gets only those.
        var readOnlyOfAdmin = TokenOf(await RunAsync("admin", "key", "create", "--user", "admin", "--name", "r", "--permissions", "read", "--expires", "1d"));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client, HttpMethod.Get, "health/details", Header(readOnlyOfAdmin)));
    }

    [Fact]
    public async Task AKey_StopsWorkingWhenItExpires()
    {
        using var server = await StartServerAsync();
        using var client = Client(server);
        await UsersAsync(("ci", "reader"));
        // Second precision on the command line: the margin covers starting the command process.
        var expiresAt = DateTime.UtcNow.AddSeconds(30);
        var expires = expiresAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        var key = TokenOf(await RunAsync("admin", "key", "create", "--user", "ci", "--name", "short", "--permissions", "read", "--expires", expires));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, "v3/index.json", Header(key)));

        var wait = expiresAt.AddSeconds(1) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, HttpMethod.Get, "v3/index.json", Header(key)));
        var shown = await RunAsync("admin", "key", "show", KeyId(key));
        Assert.Contains("Status:                expired", shown.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsageAndLookupErrors_UseTheirExitCodes_ThroughTheBuiltHost()
    {
        await UsersAsync(("ci", "reader"));

        await RunAsync(2, "admin", "key", "create", "--user", "ci", "--name", "k", "--permissions", "read");
        await RunAsync(2, "admin", "key", "create", "--user", "ci", "--name", "k", "--permissions", "read", "--expires", "2020-01-01");
        await RunAsync(4, "admin", "key", "create", "--user", "ghost", "--name", "k", "--permissions", "read", "--expires", "1d");
        await RunAsync(4, "admin", "key", "revoke", "AAAAAAAAAAAA");

        var json = await RunAsync("admin", "key", "list", "--format", "json");
        using var document = System.Text.Json.JsonDocument.Parse(json.StandardOutput);
        Assert.Empty(document.RootElement.GetProperty("keys").EnumerateArray());
    }

    private static string TokenOf(HostProcess run)
    {
        var match = TokenLine().Match(run.StandardOutput);
        Assert.True(match.Success, run.Output);
        return match.Groups[1].Value;
    }

    private static string KeyId(string token) => token.Substring(4, 12);

    private static string Secret(string token) => token[17..];

    private static Action<HttpRequestMessage> Header(string token)
        => request => request.Headers.TryAddWithoutValidation("X-NuGet-ApiKey", token);

    private static Action<HttpRequestMessage> Basic(string token)
        => request => request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("apikey:" + token)));

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, HttpMethod method, string path, Action<HttpRequestMessage> credentials)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        credentials(request);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static HttpClient Client(HostProcess server)
    {
        var address = ListeningAddress().Match(server.Output);
        Assert.True(address.Success, server.Output);
        return new HttpClient { BaseAddress = new Uri(address.Groups[1].Value) };
    }

    private async Task<HostProcess> StartServerAsync()
    {
        var server = HostProcess.Start([], HostEnvironment());
        await server.WaitForOutputAsync("Now listening");
        return server;
    }

    private async Task UsersAsync(params (string Name, string Permissions)[] users)
    {
        using (await RunWithInputAsync(["admin", "bootstrap", "--username", "admin", "--password-stdin"], Password))
        {
        }

        foreach (var (name, permissions) in users)
        {
            using (await RunWithInputAsync(["admin", "user", "create", name, "--permissions", permissions, "--password-stdin"], Password))
            {
            }
        }
    }

    private Task<HostProcess> RunAsync(params string[] arguments) => RunAsync(0, arguments);

    private async Task<HostProcess> RunAsync(int expectedExitCode, params string[] arguments)
    {
        var host = HostProcess.Start(arguments, HostEnvironment());
        var exitCode = await host.WaitForExitAsync();
        Assert.True(exitCode == expectedExitCode, $"exit {exitCode}, expected {expectedExitCode}{Environment.NewLine}{host.Output}");
        host.Dispose();
        return host;
    }

    private async Task<HostProcess> RunWithInputAsync(string[] arguments, string standardInput)
    {
        var host = HostProcess.Start(arguments, HostEnvironment(), standardInput);
        var exitCode = await host.WaitForExitAsync();
        Assert.True(exitCode == 0, host.Output);
        return host;
    }

    private Dictionary<string, string> HostEnvironment() => new()
    {
        ["Security__Database__ConnectionString"] = _database.ConnectionString,
        ["Security__Passwords__IterationCount"] = "100000",
        ["Database__ConnectionString"] = $"Data Source={Path.Combine(_database.Directory, "packages.db")}",
        ["Storage__Path"] = Path.Combine(_database.Directory, "packages"),

        // Everything is logged, so that a log line written to standard output would be caught.
        ["Logging__Console__LogLevel__Default"] = "Information",
        ["Logging__Console__LogLevel__Microsoft.EntityFrameworkCore"] = "Information",
    };

    [GeneratedRegex(@"\A(epq_[0-9A-Za-z]{12}_[0-9A-Za-z]{43})\r?\n\z")]
    private static partial Regex TokenLine();

    [GeneratedRegex(@"Now listening on: (http://127\.0\.0\.1:\d+)")]
    private static partial Regex ListeningAddress();
}
