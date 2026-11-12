using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using ElPaqueto.Security;
using ElPaqueto.Security.Authorization;
using ElPaqueto.Web;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// Deny by default on the real host: every route is protected by the permission table, the three
/// permission flags are independent, and what is not in the table is closed.
/// </summary>
public class AuthorizationIntegrationTests : IDisposable
{
    private const string Password = "a long enough passphrase for tests";

    private readonly ITestOutputHelper _output;
    private readonly ElPaquetoApplication _app;

    public AuthorizationIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _app = new ElPaquetoApplication(output);
    }

    public void Dispose() => _app.Dispose();

    public static IEnumerable<object[]> ReadRoutes() =>
    [
        ["GET", "v3/index.json"],
        ["GET", "v3/search?q=anything"],
        ["GET", "v3/autocomplete?q=anything"],
        ["GET", "v3/dependents?packageId=some.package"],
        ["GET", "v3/registration/some.package/index.json"],
        ["GET", "v3/registration/some.package/1.0.0.json"],
        ["GET", "v3/package/some.package/index.json"],
        ["GET", "v3/package/some.package/1.0.0/some.package.1.0.0.nupkg"],
        ["GET", "v3/package/some.package/1.0.0/some.package.nuspec"],
        ["GET", "v3/package/some.package/1.0.0/readme"],
        ["GET", "v3/package/some.package/1.0.0/icon"],
        ["GET", "api/download/symbols/some.pdb/0123456789abcdef0123456789abcdef1/some.pdb"],
        ["GET", "api/download/symbols/ab/some.pdb/0123456789abcdef0123456789abcdef1/some.pdb"],
        ["GET", "/"],
        ["GET", "packages/some.package"],
        ["GET", "upload"],
        ["GET", "stats"],
        ["HEAD", "v3/search?q=anything"],
    ];

    public static IEnumerable<object[]> WriteRoutes() =>
    [
        ["PUT", "api/v2/package"],
        ["PUT", "api/v2/symbol"],
        ["POST", "api/v2/package/some.package/1.0.0"],
        ["DELETE", "api/v2/package/some.package/1.0.0"],
    ];

    [Theory]
    [MemberData(nameof(ReadRoutes))]
    [MemberData(nameof(WriteRoutes))]
    public async Task Anonymous_IsAnsweredWith401OnEveryRoute(string method, string path)
    {
        using var client = _app.CreateAnonymousClient();

        using var response = await Send(client, method, path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ReadRoutes))]
    [MemberData(nameof(WriteRoutes))]
    public async Task AUserWithNoPermission_IsAnsweredWith403OnEveryRoute(string method, string path)
    {
        await _app.AddUserAsync("nobody", Password, Permission.None);
        using var client = _app.CreateClientFor("nobody", Password);

        using var response = await Send(client, method, path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("WWW-Authenticate"), "A 403 must not carry a challenge.");
    }

    [Theory]
    [MemberData(nameof(ReadRoutes))]
    public async Task ReadPermission_OpensEveryReadRoute(string method, string path)
    {
        await _app.AddUserAsync("reader", Password, Permission.Read);
        using var client = _app.CreateClientFor("reader", Password);

        using var response = await Send(client, method, path);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task ReadPermission_NeverOpensAWriteRoute(string method, string path)
    {
        await _app.AddUserAsync("reader", Password, Permission.Read);
        using var client = _app.CreateClientFor("reader", Password);

        using var response = await Send(client, method, path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task PublishPermission_OpensTheWriteRoutes(string method, string path)
    {
        await _app.AddUserAsync("publisher", Password, Permission.Publish);
        using var client = _app.CreateClientFor("publisher", Password);

        using var response = await Send(client, method, path);

        // Authorized: the action itself may answer 400 or 404 for an empty or unknown package.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ReadRoutes))]
    public async Task PublishPermission_DoesNotImplyRead(string method, string path)
    {
        await _app.AddUserAsync("publisher", Password, Permission.Publish);
        using var client = _app.CreateClientFor("publisher", Password);

        using var response = await Send(client, method, path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminPermission_ImpliesNeitherReadNorPublish()
    {
        await _app.AddUserAsync("admin-only", Password, Permission.Admin);
        using var client = _app.CreateClientFor("admin-only", Password);

        using var read = await Send(client, "GET", "v3/index.json");
        using var write = await Send(client, "PUT", "api/v2/package");

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task AMethodTheRouteDoesNotAccept_IsRefusedBeforeTheAction()
    {
        using var anonymous = _app.CreateAnonymousClient();
        using var authenticated = _app.CreateClient();

        // The inherited route accepted any verb: POST /v3/search used to reach the action.
        using var anonymousPost = await Send(anonymous, "POST", "v3/search?q=anything");
        using var authenticatedPost = await Send(authenticated, "POST", "v3/search?q=anything");
        using var authenticatedPut = await Send(authenticated, "PUT", "v3/index.json");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousPost.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, authenticatedPost.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, authenticatedPut.StatusCode);
    }

    [Fact]
    public async Task AnUnknownUrl_RevealsNothingToAnonymousCallers()
    {
        using var anonymous = _app.CreateAnonymousClient();
        using var authenticated = _app.CreateClient();

        using var anonymousResponse = await Send(anonymous, "GET", "does/not/exist");
        using var authenticatedResponse = await Send(authenticated, "GET", "does/not/exist");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, authenticatedResponse.StatusCode);
    }

    [Fact]
    public async Task ThePermissionsOfAUserAreReadOnEveryRequest()
    {
        await _app.AddUserAsync("promoted", Password, Permission.None);
        using var client = _app.CreateClientFor("promoted", Password);

        using var before = await Send(client, "GET", "v3/index.json");
        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);

        using (var scope = _app.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ElPaqueto.Security.Data.AuthDbContext>();
            var user = database.Users.Single(u => u.NormalizedUserName == "PROMOTED");
            user.Permissions = Permission.Read;
            await database.SaveChangesAsync();
        }

        using var after = await Send(client, "GET", "v3/index.json");
        Assert.NotEqual(HttpStatusCode.Forbidden, after.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task HardDelete_NeedsAdminOnTopOfPublish()
    {
        using var app = new ElPaquetoApplication(
            _output,
            inMemoryConfiguration: dict => dict["PackageDeletionBehavior"] = "HardDelete");
        await app.AddUserAsync("publisher", Password, Permission.Publish);
        await app.AddUserAsync("publisher-admin", Password, Permission.Publish | Permission.Admin);
        using var publisher = app.CreateClientFor("publisher", Password);
        using var publisherAdmin = app.CreateClientFor("publisher-admin", Password);

        using var refused = await Send(publisher, "DELETE", "api/v2/package/some.package/1.0.0");
        using var allowed = await Send(publisherAdmin, "DELETE", "api/v2/package/some.package/1.0.0");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, allowed.StatusCode);
    }

    [Fact]
    public async Task Unlisting_NeedsOnlyPublishByDefault()
    {
        await _app.AddUserAsync("publisher", Password, Permission.Publish);
        using var publisher = _app.CreateClientFor("publisher", Password);

        using var response = await Send(publisher, "DELETE", "api/v2/package/some.package/1.0.0");

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Liveness_IsAnonymousAndRevealsOnlyTheOverallStatus()
    {
        using var client = _app.CreateAnonymousClient();

        using var response = await client.GetAsync("health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"Status\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Sqlite", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Filesystem", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HealthDetails_NeedAdmin()
    {
        await _app.AddUserAsync("reader", Password, Permission.Read);
        using var anonymous = _app.CreateAnonymousClient();
        using var reader = _app.CreateClientFor("reader", Password);
        using var admin = _app.CreateClient();

        using var anonymousResponse = await anonymous.GetAsync("health/details");
        using var readerResponse = await reader.GetAsync("health/details");
        using var adminResponse = await admin.GetAsync("health/details");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, readerResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
    }

    [Fact]
    public void EveryMatchableEndpoint_HasARowInThePermissionTable()
    {
        var endpoints = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var unmapped = EndpointInventory.FindUnmapped(endpoints);

        Assert.True(unmapped.Count == 0, "Endpoints without a row in the permission table (denied): " + string.Join("; ", unmapped));
    }

    [Fact]
    public void EveryRowOfThePermissionTable_MatchesARealEndpoint()
    {
        var endpoints = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var unused = EndpointInventory.FindUnusedRules(endpoints);

        Assert.True(unused.Count == 0, "Rows of the permission table that no endpoint uses: " + string.Join("; ", unused));
    }

    [Fact]
    public void EveryRouteNameOfTheWebProject_HasARowAndNoRowIsInvented()
    {
        var routeNames = typeof(Routes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        var tableKeys = EndpointPermissionTable.Rules
            .Select(rule => rule.Key)
            .Where(key => !key.StartsWith("page:", StringComparison.Ordinal) && !key.StartsWith("health", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(routeNames.OrderBy(n => n, StringComparer.Ordinal), tableKeys.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void NoEndpoint_IsMarkedAnonymousByAttribute()
    {
        var endpoints = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var anonymous = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null)
            .Select(endpoint => endpoint.DisplayName)
            .ToList();

        Assert.Empty(anonymous);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "PUT" or "POST")
        {
            request.Content = new ByteArrayContent([]);
        }

        return client.SendAsync(request);
    }
}
