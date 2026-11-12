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
/// Only authentication is checked: <c>401</c> for bad credentials, <c>200</c> for good ones. Whether a
/// permission grants access to an endpoint is the job of authorization, which is not part of this change.
/// </remarks>
public class AdminCommandAuthenticationIntegrationTests : IDisposable
{
    private const string AdminPassword = "the first administrator passphrase";

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

    private async Task<HttpStatusCode> GetIndexAsync(string userName, string password)
    {
        using var client = _app.CreateAnonymousClient();
        if (userName is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}")));
        }

        using var response = await client.GetAsync("v3/index.json");
        return response.StatusCode;
    }
}
