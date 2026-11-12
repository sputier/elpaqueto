using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using ElPaqueto.Security.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// Users administered with the real <c>elpaqueto admin</c> commands (a separate process) authenticate
/// against the real server sharing the same security database.
/// </summary>
/// <remarks>
/// Only authentication is checked: <c>401</c> for bad credentials, <c>200</c> for good ones. Whether a
/// permission grants access to an endpoint is the job of authorization, which is not part of this change,
/// so permission changes are observed through the permission claims of the authenticated caller, read by
/// a test-only endpoint.
/// </remarks>
public class AdminCommandAuthenticationIntegrationTests : IDisposable
{
    private const string AdminPassword = "the first administrator passphrase";
    private const string UserPassword = "the passphrase of alice";
    private const string NewUserPassword = "the new passphrase of alice";
    private const string PermissionsPath = "/test-only/permissions";

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

    [Fact]
    public async Task ACreatedReader_AuthenticatesWithReadOnly()
    {
        await CreateAliceAsync("reader");

        Assert.Equal(HttpStatusCode.OK, await GetIndexAsync("alice", UserPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetIndexAsync("alice", NewUserPassword));
        Assert.Equal("Read", await GetPermissionsAsync("alice", UserPassword));
    }

    [Fact]
    public async Task Disable_RejectsTheNextRequest_AndEnable_AcceptsItAgain()
    {
        await CreateAliceAsync("reader");
        using var client = AuthenticatedClient(_app, "alice", UserPassword);
        Assert.Equal(HttpStatusCode.OK, await GetIndexAsync(client));

        await _commands.RunAsync(string.Empty, 0, "admin", "user", "disable", "alice");

        // The same client, with the password it was just accepted with: the verification cache must not hide the change.
        Assert.Equal(HttpStatusCode.Unauthorized, await GetIndexAsync(client));

        await _commands.RunAsync(string.Empty, 0, "admin", "user", "enable", "alice");

        Assert.Equal(HttpStatusCode.OK, await GetIndexAsync(client));
    }

    [Fact]
    public async Task SetPermissions_AppliesToTheNextRequest()
    {
        await CreateAliceAsync("reader");
        using var app = WithPermissionsEndpoint();
        using var client = AuthenticatedClient(app, "alice", UserPassword);
        Assert.Equal("Read", await client.GetStringAsync(PermissionsPath));

        await _commands.RunAsync(string.Empty, 0, "admin", "user", "set-permissions", "alice", "--permissions", "publisher");

        Assert.Equal("Read,Publish", await client.GetStringAsync(PermissionsPath));

        await _commands.RunAsync(string.Empty, 0, "admin", "user", "set-permissions", "alice", "--permissions", "publish");

        Assert.Equal("Publish", await client.GetStringAsync(PermissionsPath));
    }

    [Fact]
    public async Task ResetPassword_RejectsTheOldPasswordAndAcceptsTheNewOneOnTheNextRequest()
    {
        await CreateAliceAsync("reader");

        // Signed in once, so the old password is in the verification cache.
        using var oldClient = AuthenticatedClient(_app, "alice", UserPassword);
        Assert.Equal(HttpStatusCode.OK, await GetIndexAsync(oldClient));

        await _commands.RunAsync(NewUserPassword + "\n", 0, "admin", "user", "reset-password", "alice", "--password-stdin");

        Assert.Equal(HttpStatusCode.Unauthorized, await GetIndexAsync(oldClient));
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

    private async Task<string> GetPermissionsAsync(string userName, string password)
    {
        using var app = WithPermissionsEndpoint();
        using var client = AuthenticatedClient(app, userName, password);
        return await client.GetStringAsync(PermissionsPath);
    }

    /// <summary>
    /// The server with an extra endpoint that authenticates the caller with the default scheme and
    /// returns its permission claims, comma-separated.
    /// </summary>
    private WebApplicationFactory<Startup> WithPermissionsEndpoint()
        => _app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter, PermissionsEndpointStartupFilter>()));

    private static HttpClient AuthenticatedClient(WebApplicationFactory<Startup> app, string userName, string password)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}")));
        return client;
    }

    private async Task<HttpStatusCode> GetIndexAsync(string userName, string password)
    {
        if (userName is null)
        {
            using var anonymous = _app.CreateAnonymousClient();
            return await GetIndexAsync(anonymous);
        }

        using var client = AuthenticatedClient(_app, userName, password);
        return await GetIndexAsync(client);
    }

    private static async Task<HttpStatusCode> GetIndexAsync(HttpClient client)
    {
        using var response = await client.GetAsync("v3/index.json");
        return response.StatusCode;
    }

    private sealed class PermissionsEndpointStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map(PermissionsPath, branch => branch.Run(async context =>
            {
                var result = await context.AuthenticateAsync();
                if (!result.Succeeded)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                var permissions = result.Principal.FindAll(ElPaquetoClaimTypes.Permission).Select(claim => claim.Value);
                await context.Response.WriteAsync(string.Join(",", permissions));
            }));

            next(app);
        };
    }
}
