using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ElPaqueto.Security;
using ElPaqueto.Security.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// The 401/403 response contract of ADR 0002, decision 11 (#28): each row on a read route, a write route and a
/// Razor page, never a redirect, and the exact challenge and bodies.
/// </summary>
/// <remarks>
/// Every client here is created with <c>AllowAutoRedirect = false</c> (or sends through the test server's handler,
/// which never follows a redirect): a 3xx or a <c>Location</c> header would reach the assertions.
/// </remarks>
public class ResponseContractIntegrationTests : IDisposable
{
    private const string Password = "a long enough passphrase for tests";
    private const string Challenge = "Basic realm=\"ElPaqueto\"";

    // A documentation address (RFC 5737), so that the throttle has a client to count; it is not loopback, so the
    // requests are sent as HTTPS.
    private const string ClientAddress = "203.0.113.10";

    private readonly ElPaquetoApplication _app;

    public ResponseContractIntegrationTests(ITestOutputHelper output)
    {
        _app = new ElPaquetoApplication(output);
    }

    public void Dispose() => _app.Dispose();

    public static IEnumerable<object[]> RouteClasses() => [["read route"], ["write route"], ["Razor page"]];

    /// <summary>Every route and page of the permission table, plus what is denied because it has no row.</summary>
    public static IEnumerable<object[]> EveryRoute() =>
    [
        ["GET", "v3/index.json"],
        ["GET", "v3/search?q=TestData"],
        ["HEAD", "v3/search?q=TestData"],
        ["GET", "v3/autocomplete?q=TestData"],
        ["HEAD", "v3/autocomplete?q=TestData"],
        ["GET", "v3/dependents?packageId=TestData"],
        ["GET", "v3/registration/testdata/index.json"],
        ["GET", "v3/registration/testdata/1.2.3.json"],
        ["GET", "v3/package/testdata/index.json"],
        ["HEAD", "v3/package/testdata/index.json"],
        ["GET", "v3/package/testdata/1.2.3/testdata.1.2.3.nupkg"],
        ["HEAD", "v3/package/testdata/1.2.3/testdata.1.2.3.nupkg"],
        ["GET", "v3/package/testdata/1.2.3/testdata.nuspec"],
        ["GET", "v3/package/testdata/1.2.3/readme"],
        ["GET", "v3/package/testdata/1.2.3/icon"],
        ["GET", "api/download/symbols/testdata.pdb/16F71ED8DD574AA2AD4A22D29E9C981Bffffffff/testdata.pdb"],
        ["GET", "api/download/symbols/testprefix/testdata.pdb/16F71ED8DD574AA2AD4A22D29E9C981Bffffffff/testdata.pdb"],
        ["PUT", "api/v2/package"],
        ["PUT", "api/v2/symbol"],
        ["POST", "api/v2/package/testdata/1.2.3"],
        ["DELETE", "api/v2/package/testdata/1.2.3"],
        ["GET", "/"],
        ["HEAD", "/"],
        ["GET", "packages/TestData"],
        ["GET", "packages/TestData/1.2.3"],
        ["GET", "upload"],
        ["GET", "stats"],
        ["GET", "health/details"],
        ["HEAD", "v3/index.json"],
        ["POST", "v3/search?q=TestData"],
        ["GET", "does/not/exist"],
        ["GET", "Account/Login"],
    ];

    [Theory]
    [MemberData(nameof(RouteClasses))]
    public async Task Row401_NoOrFailedCredentials_IsAChallengeWithNoBodyAndNoRedirect(string routeClass)
    {
        await SeedAsync();
        using var client = _app.CreateClientWithoutRedirects();
        var (method, path) = Route(routeClass, missing: false);

        var cases = new (string Name, Action<HttpRequestMessage> Credentials)[]
        {
            ("no credentials", null),
            ("wrong password", r => r.Headers.Authorization = TestUserExtensions.Basic(_app.TestUserName, _app.TestPassword + "x")),
            ("malformed header", r => r.Headers.TryAddWithoutValidation("Authorization", "Basic !!!")),
            ("invalid key in X-NuGet-ApiKey", r => r.Headers.TryAddWithoutValidation(TestUserExtensions.ApiKeyHeader, "epq_not-a-real-key")),
        };

        string reference = null;
        foreach (var (name, credentials) in cases)
        {
            using var response = await ResponseSnapshot.SendAsync(client, method, path, credentials);

            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{name}: {method} {path} answered {(int)response.StatusCode}.");
            Assert.Equal(new[] { Challenge }, response.Headers.GetValues("WWW-Authenticate"));
            await AssertNoBodyAndNoRedirectAsync(response);

            // Identical whatever the reason.
            var fingerprint = await ResponseSnapshot.FingerprintAsync(response);
            reference ??= fingerprint;
            Assert.Equal(reference, fingerprint);
        }
    }

    [Theory]
    [MemberData(nameof(RouteClasses))]
    public async Task Row403_AuthenticatedWithoutThePermission_HasNoChallengeNoBodyAndNoRedirect(string routeClass)
    {
        await SeedAsync();
        await _app.AddUserAsync("publisher", Password, Permission.Publish);
        await _app.AddUserAsync("reader", Password, Permission.Read);
        var readKey = await _app.AddApiKeyAsync("reader", Permission.Read);
        using var client = _app.CreateClientWithoutRedirects();
        var (method, path) = Route(routeClass, missing: false);

        // Reads need Read (the Publish user lacks it); writes need a Publish key (the Read key lacks it).
        Action<HttpRequestMessage> credentials = routeClass == "write route"
            ? r => r.Headers.TryAddWithoutValidation(TestUserExtensions.ApiKeyHeader, readKey.Token)
            : r => r.Headers.Authorization = TestUserExtensions.Basic("publisher", Password);

        using var response = await ResponseSnapshot.SendAsync(client, method, path, credentials);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("WWW-Authenticate"), "A 403 must not carry a challenge.");
        await AssertNoBodyAndNoRedirectAsync(response);
    }

    [Theory]
    [MemberData(nameof(RouteClasses))]
    public async Task Row429_Throttled_HasRetryAfterNoChallengeNoBodyAndNoRedirect(string routeClass)
    {
        await SeedAsync();
        using var client = ClientFrom(ClientAddress);
        var (method, path) = Route(routeClass, missing: false);

        // Five distinct wrong passwords (the default pair limit) from one address throttle that user there.
        for (var i = 0; i < 5; i++)
        {
            using var failure = await ResponseSnapshot.SendAsync(client, "GET", "v3/index.json", Basic(_app.TestPassword + "-wrong-" + i));
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }

        // The right password, on the route under test: refused before any check, whatever the route.
        using var response = await ResponseSnapshot.SendAsync(client, method, path, Basic(_app.TestPassword));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter?.Delta);
        Assert.InRange(response.Headers.RetryAfter.Delta.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        Assert.False(response.Headers.Contains("WWW-Authenticate"), "A 429 must not carry a challenge.");
        await AssertNoBodyAndNoRedirectAsync(response);

        Action<HttpRequestMessage> Basic(string password)
            => r => r.Headers.Authorization = TestUserExtensions.Basic(_app.TestUserName, password);
    }

    [Theory]
    [MemberData(nameof(RouteClasses))]
    public async Task Row404_AuthorizedButMissing_KeepsTheUpstreamBehaviour(string routeClass)
    {
        await SeedAsync();
        using var client = _app.CreateClientWithoutRedirects();
        var (method, path) = Route(routeClass, missing: true);

        // The test user's key, in X-NuGet-ApiKey, holds every permission: it is authorized everywhere.
        using var response = await ResponseSnapshot.SendAsync(
            client, method, path, r => r.Headers.TryAddWithoutValidation(TestUserExtensions.ApiKeyHeader, _app.TestApiKey));

        Assert.False(response.Headers.Contains("WWW-Authenticate"));
        Assert.Null(response.Headers.Location);
        if (routeClass == "Razor page")
        {
            // Upstream's package page answers a missing package with its own "not found" page, not a 404.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("package not found", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task TheRoutesOfTheRedirectTest_CoverEveryRowOfThePermissionTable()
    {
        await SeedAsync();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in EveryRoute())
        {
            var (method, path) = ((string)row[0], (string)row[1]);
            var uri = new Uri(_app.Server.BaseAddress, path);
            var context = await _app.Server.SendAsync(c =>
            {
                c.Request.Method = method;
                c.Request.Path = uri.AbsolutePath;
                c.Request.QueryString = new QueryString(uri.Query);
            });

            if (EndpointKey.Resolve(context.GetEndpoint()) is { } key)
            {
                covered.Add(key);
            }
        }

        // The liveness endpoint is the one row that is never refused.
        var expected = EndpointPermissionTable.Rules.Where(rule => !rule.Anonymous).Select(rule => rule.Key);

        Assert.Empty(expected.Except(covered));
    }

    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task NoRefusal_IsARedirect_OnAnyRouteOrPage(string method, string path)
    {
        await SeedAsync();
        await _app.AddUserAsync("nobody", Password, Permission.None);
        using var client = _app.CreateClientWithoutRedirects();

        using var anonymous = await ResponseSnapshot.SendAsync(client, method, path);
        using var wrongPassword = await ResponseSnapshot.SendAsync(
            client, method, path, r => r.Headers.Authorization = TestUserExtensions.Basic("nobody", Password + "x"));
        using var withoutPermission = await ResponseSnapshot.SendAsync(
            client, method, path, r => r.Headers.Authorization = TestUserExtensions.Basic("nobody", Password));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, withoutPermission.StatusCode);
        foreach (var response in new[] { anonymous, wrongPassword, withoutPermission })
        {
            await AssertNoBodyAndNoRedirectAsync(response);
        }
    }

    [Fact]
    public async Task TheChallenge_CarriesNoCharsetParameter_AsDecidedByTheAdr()
    {
        // ADR 0002, decision 11: the challenge is exactly Basic realm="ElPaqueto", without the charset="UTF-8"
        // parameter of RFC 7617, until stock NuGet clients are shown to accept it. The ADR wins over the table of
        // the original text of #28; the amended criterion is recorded on the issue.
        using var client = _app.CreateClientWithoutRedirects();

        using var response = await client.GetAsync("v3/index.json");

        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Basic", challenge.Scheme);
        Assert.Equal("realm=\"ElPaqueto\"", challenge.Parameter);
        Assert.DoesNotContain("charset", response.Headers.GetValues("WWW-Authenticate").Single(), StringComparison.OrdinalIgnoreCase);
    }

    // The refusals carry no body, hence no Content-Type (and no charset) at all: nothing to parse, nothing that
    // differs between causes, no stack trace, user name or reason.
    private static async Task AssertNoBodyAndNoRedirectAsync(HttpResponseMessage response)
    {
        Assert.False((int)response.StatusCode is >= 300 and < 400, $"Redirect: {(int)response.StatusCode}.");
        Assert.Null(response.Headers.Location);
        Assert.Null(response.Content.Headers.ContentType);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    private static (string Method, string Path) Route(string routeClass, bool missing) => routeClass switch
    {
        "read route" => ("GET", missing ? "v3/package/missing.package/index.json" : "v3/package/testdata/index.json"),
        "write route" => ("DELETE", missing ? "api/v2/package/missing.package/1.0.0" : "api/v2/package/testdata/1.2.3"),
        "Razor page" => ("GET", missing ? "packages/missing.package" : "packages/TestData"),
        _ => throw new ArgumentOutOfRangeException(nameof(routeClass), routeClass, null),
    };

    // A client whose requests come from the given address over HTTPS, as through Kestrel. The test server's handler
    // never follows redirects.
    private HttpClient ClientFrom(string address)
    {
        var handler = _app.Server.CreateHandler(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(address);
            context.Request.Scheme = "https";
        });

        return new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
    }

    private async Task SeedAsync()
    {
        await _app.AddPackageAsync(TestResources.GetResourceStream(TestResources.Package));
        await _app.AddSymbolPackageAsync(TestResources.GetResourceStream(TestResources.SymbolPackage));
    }
}
