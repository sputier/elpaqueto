using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// Users administered with the real <c>elpaqueto admin</c> commands (a separate process) authenticate
/// against the real server sharing the same security database.
/// </summary>
/// <remarks>
/// <c>v3/index.json</c> needs <c>Read</c> and <c>health/details</c> needs <c>Admin</c> (deny-by-default
/// authorization): <c>401</c> means the credentials were refused, <c>403</c> that they were accepted
/// without the permission.
/// </remarks>
public class AdminCommandAuthenticationIntegrationTests : IDisposable
{
    private const string AdminPassword = "the first administrator passphrase";
    private const string UserPassword = "the passphrase of alice";
    private const string NewUserPassword = "the new passphrase of alice";

    private readonly AdminCommandLine _commands = new();
    private readonly ElPaquetoApplication _app;

    public AdminCommandAuthenticationIntegrationTests(ITestOutputHelper output)
    {
        // No seeded user: the command line creates every user of this server.
        _app = new ElPaquetoApplication(output, inMemoryConfiguration: configuration =>
        {
            configuration["Security:Database:ConnectionString"] = _commands.SecurityConnectionString;
            configuration["Security:Passwords:IterationCount"] = AdminCommandLine.IterationCount;
        }, seedTestUser: false);
    }

    public void Dispose()
    {
        _app.Dispose();
        _commands.Dispose();
    }

    [Fact]
    public async Task TheBootstrappedAdministrator_AuthenticatesWithBasic()
    {
        await _commands.RunAsync(AdminPassword + "\n", 0, "admin", "bootstrap", "--username", "admin", "--password-stdin");

        Assert.Equal(HttpStatusCode.OK, await GetIndexAsync("admin", AdminPassword));
    }

    [Fact]
    public async Task TheBootstrappedAdministrator_HoldsTheAdminPermission()
    {
        await _commands.RunAsync(AdminPassword + "\n", 0, "admin", "bootstrap", "--username", "admin", "--password-stdin");

        using var client = Client("admin", AdminPassword);

        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "health/details"));
    }

    [Fact]
    public async Task TheBootstrappedAdministrator_WithAWrongPassword_IsRejected()
    {
        await _commands.RunAsync(AdminPassword + "\n", 0, "admin", "bootstrap", "--username", "admin", "--password-stdin");

        Assert.Equal(HttpStatusCode.Unauthorized, await GetIndexAsync("admin", AdminPassword + "x"));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetIndexAsync("someone-else", AdminPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetIndexAsync(userName: null, password: null));
    }

    [Fact]
    public async Task TheBootstrappedAdministrator_SignsInWhileTheServerIsAlreadyRunning()
    {
        // The server starts first, on an empty security database, as in a fresh deployment.
        Assert.Equal(HttpStatusCode.Unauthorized, await GetIndexAsync("admin", AdminPassword));

        await _commands.RunAsync(AdminPassword + "\n", 0, "admin", "bootstrap", "--username", "admin", "--password-stdin");

        Assert.Equal(HttpStatusCode.OK, await GetIndexAsync("admin", AdminPassword));
    }

    [Fact]
    public async Task UserNamesAcceptedByTheCommand_AreAcceptedBySignIn()
    {
        // The longest name and every allowed character class: the command and the Basic handler share one rule.
        var userName = "Ad.min_-9" + new string('x', 55);
        Assert.Equal(64, userName.Length);

        await _commands.RunAsync(AdminPassword + "\n", 0, "admin", "bootstrap", "--username", userName, "--password-stdin");

        Assert.Equal(HttpStatusCode.OK, await GetIndexAsync(userName, AdminPassword));
    }

    [Fact]
    public async Task ACreatedReader_CanReadButNotAdminister()
    {
        await CreateAliceAsync("reader");
        using var client = Client("alice", UserPassword);

        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "v3/index.json"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(client, "health/details"));
    }

    [Fact]
    public async Task Disable_RejectsTheNextRequest_AndEnable_AcceptsItAgain()
    {
        await CreateAliceAsync("reader");
        using var client = Client("alice", UserPassword);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "v3/index.json"));

        await _commands.RunAsync(string.Empty, 0, "admin", "user", "disable", "alice");

        // The same client, with the password it was just accepted with: the verification cache must not hide the change.
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(client, "v3/index.json"));

        await _commands.RunAsync(string.Empty, 0, "admin", "user", "enable", "alice");

        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "v3/index.json"));
    }

    [Fact]
    public async Task SetPermissions_AppliesToTheNextRequest()
    {
        await CreateAliceAsync("reader");
        using var client = Client("alice", UserPassword);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "v3/index.json"));

        // Read removed: still authenticated, no longer allowed to read.
        await _commands.RunAsync(string.Empty, 0, "admin", "user", "set-permissions", "alice", "--permissions", "publish");

        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(client, "v3/index.json"));

        await _commands.RunAsync(string.Empty, 0, "admin", "user", "set-permissions", "alice", "--permissions", "read,admin");

        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "v3/index.json"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "health/details"));
    }

    [Fact]
    public async Task ResetPassword_RejectsTheOldPasswordAndAcceptsTheNewOneOnTheNextRequest()
    {
        await CreateAliceAsync("reader");

        // Signed in once, so the old password is in the verification cache.
        using var oldClient = Client("alice", UserPassword);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(oldClient, "v3/index.json"));

        await _commands.RunAsync(NewUserPassword + "\n", 0, "admin", "user", "reset-password", "alice", "--password-stdin");

        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(oldClient, "v3/index.json"));
        Assert.Equal(HttpStatusCode.OK, await GetIndexAsync("alice", NewUserPassword));
    }

    [Fact]
    public async Task ResetPassword_OfADisabledUser_DoesNotLetItSignIn()
    {
        await CreateAliceAsync("reader");
        await _commands.RunAsync(string.Empty, 0, "admin", "user", "disable", "alice");

        await _commands.RunAsync(NewUserPassword + "\n", 0, "admin", "user", "reset-password", "alice", "--password-stdin");

        Assert.Equal(HttpStatusCode.Unauthorized, await GetIndexAsync("alice", NewUserPassword));
    }

    private Task CreateAliceAsync(string permissions)
        => _commands.RunAsync(UserPassword + "\n", 0, "admin", "user", "create", "alice", "--permissions", permissions, "--password-stdin");

    private HttpClient Client(string userName, string password)
    {
        var client = _app.CreateAnonymousClient();
        if (userName is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}")));
        }

        return client;
    }

    private async Task<HttpStatusCode> GetIndexAsync(string userName, string password)
    {
        using var client = Client(userName, password);
        return await GetAsync(client, "v3/index.json");
    }

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        return response.StatusCode;
    }
}
