using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// Cross-origin access (#29) on the real host: nothing is allowed by default, configured origins
/// are allowed without credentials, and preflights are answered before authorization.
/// </summary>
public class CorsIntegrationTests
{
    private const string AllowedOrigin = "https://ui.example.test";
    private const string OtherOrigin = "https://evil.example.com";

    private readonly ITestOutputHelper _output;

    public CorsIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static IEnumerable<object[]> Routes() =>
    [
        ["v3/index.json"],
        ["v3/package/testdata/1.2.3/testdata.1.2.3.nupkg"],
        ["packages/TestData"],
        ["health"],
        ["no/such/route"],
    ];

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task ByDefault_APreflightGetsNoApproval(string path)
    {
        using var app = new ElPaquetoApplication(_output);
        using var client = app.CreateAnonymousClient();

        using var response = await client.SendAsync(Preflight(path, OtherOrigin));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertNoCorsHeaders(response);
    }

    [Fact]
    public async Task ByDefault_ACrossOriginRequestGetsNoCorsHeaders()
    {
        using var app = new ElPaquetoApplication(_output);
        using var client = app.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "v3/index.json");
        request.Headers.Add("Origin", OtherOrigin);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoCorsHeaders(response);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task AnAllowedOrigin_GetsItsPreflightApprovedWithoutCredentials(string path)
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: AllowUiOrigin);
        using var client = app.CreateAnonymousClient();

        using var response = await client.SendAsync(Preflight(path, AllowedOrigin, "GET", "authorization"));

        // Answered by CORS before authorization: no 401 for a preflight, which never carries credentials.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(AllowedOrigin, Single(response, "Access-Control-Allow-Origin"));
        Assert.Contains("GET", Single(response, "Access-Control-Allow-Methods"), StringComparison.Ordinal);
        Assert.Equal("authorization", Single(response, "Access-Control-Allow-Headers"), ignoreCase: true);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.False(response.Headers.Contains("WWW-Authenticate"));
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task AnotherOrigin_IsRefusedWhenOriginsAreConfigured(string path)
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: AllowUiOrigin);
        using var client = app.CreateAnonymousClient();

        using var response = await client.SendAsync(Preflight(path, OtherOrigin, "GET", "authorization"));

        AssertNoCorsHeaders(response);
    }

    [Theory]
    [InlineData("https://ui.example.test.evil.example.com")]
    [InlineData("http://ui.example.test")]
    [InlineData("https://ui.example.test:8443")]
    [InlineData("https://sub.ui.example.test")]
    [InlineData("null")]
    public async Task OnlyTheExactOrigin_IsAllowed(string origin)
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: AllowUiOrigin);
        using var client = app.CreateAnonymousClient();

        using var response = await client.SendAsync(Preflight("v3/index.json", origin, "GET"));

        AssertNoCorsHeaders(response);
    }

    [Fact]
    public async Task AnAllowedOrigin_CanReadAnAuthenticatedResponse_WithoutCredentialsMode()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: AllowUiOrigin);
        using var client = app.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "v3/index.json");
        request.Headers.Add("Origin", AllowedOrigin);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AllowedOrigin, Single(response, "Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task AnAllowedOrigin_SeesTheChallenge_BecauseCorsRunsBeforeAuthorization()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: AllowUiOrigin);
        using var client = app.CreateAnonymousClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "v3/index.json");
        request.Headers.Add("Origin", AllowedOrigin);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(AllowedOrigin, Single(response, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task APreflightIsNeverAnAuthenticatedRequest()
    {
        // A preflight answered by CORS never reaches an endpoint, even with credentials.
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: AllowUiOrigin);
        using var client = app.CreateClient();

        using var response = await client.SendAsync(Preflight("v3/index.json", AllowedOrigin, "GET"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(body);
    }

    [Fact]
    public async Task MethodsAndHeadersOutsideTheAllowList_AreNotApproved()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: AllowUiOrigin);
        using var client = app.CreateAnonymousClient();

        using var put = await client.SendAsync(Preflight("api/v2/package", AllowedOrigin, "PUT"));
        using var apiKey = await client.SendAsync(Preflight("v3/index.json", AllowedOrigin, "GET", "x-nuget-apikey"));

        Assert.False(
            put.Headers.TryGetValues("Access-Control-Allow-Methods", out var methods) && methods.Any(m => m.Contains("PUT", StringComparison.Ordinal)),
            "PUT is not in the default method list.");
        Assert.False(
            apiKey.Headers.TryGetValues("Access-Control-Allow-Headers", out var headers) && headers.Any(h => h.Contains("x-nuget-apikey", StringComparison.OrdinalIgnoreCase)),
            "X-NuGet-ApiKey is not in the default header list.");
    }

    [Fact]
    public async Task ConfiguredMethodsAndHeaders_ReplaceTheDefaults()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: dict =>
        {
            AllowUiOrigin(dict);
            dict["Security:Cors:AllowedMethods:0"] = "PUT";
            dict["Security:Cors:AllowedHeaders:0"] = "X-NuGet-ApiKey";
        });
        using var client = app.CreateAnonymousClient();

        using var put = await client.SendAsync(Preflight("api/v2/package", AllowedOrigin, "PUT", "x-nuget-apikey"));
        using var get = await client.SendAsync(Preflight("v3/index.json", AllowedOrigin, "GET", "authorization"));

        Assert.Equal(AllowedOrigin, Single(put, "Access-Control-Allow-Origin"));
        Assert.Contains("PUT", Single(put, "Access-Control-Allow-Methods"), StringComparison.Ordinal);
        Assert.False(
            get.Headers.TryGetValues("Access-Control-Allow-Headers", out var headers) && headers.Any(h => h.Contains("authorization", StringComparison.OrdinalIgnoreCase)),
            "A configured header list replaces the default one.");
    }

    [Fact]
    public async Task AConfiguredOrigin_IsNormalized()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: dict =>
        {
            dict["Security:Cors:AllowedOrigins:0"] = "HTTPS://UI.Example.Test:443/";
        });
        using var client = app.CreateAnonymousClient();

        using var response = await client.SendAsync(Preflight("v3/index.json", AllowedOrigin, "GET"));

        Assert.Equal(AllowedOrigin, Single(response, "Access-Control-Allow-Origin"));
    }

    private static void AllowUiOrigin(Dictionary<string, string> dict)
    {
        dict["Security:Cors:AllowedOrigins:0"] = AllowedOrigin;
    }

    private static HttpRequestMessage Preflight(string path, string origin, string method = "GET", string headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        if (headers is not null)
        {
            request.Headers.Add("Access-Control-Request-Headers", headers);
        }

        return request;
    }

    private static void AssertNoCorsHeaders(HttpResponseMessage response)
    {
        var corsHeaders = response.Headers
            .Where(h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Key)
            .ToList();

        Assert.True(corsHeaders.Count == 0, "Unexpected CORS headers: " + string.Join(", ", corsHeaders));
    }

    private static string Single(HttpResponseMessage response, string header)
    {
        var values = response.Headers.TryGetValues(header, out var found) ? found.ToList() : [];
        Assert.True(values.Count == 1, $"Expected exactly one {header} header, got {values.Count}.");
        return values[0];
    }
}
