using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using ElPaqueto.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// Security response headers (#29) on the real host, per class of response.
/// </summary>
public class SecurityHeadersIntegrationTests
{
    private const string HtmlPolicy =
        "default-src 'self'; script-src 'self' 'unsafe-inline' 'unsafe-eval'; style-src 'self'; "
        + "img-src 'self' data: https:; font-src 'self' https://static2.sharepointonline.com https://res-1.cdn.office.net; connect-src 'self'; "
        + "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    private const string ResourcePolicy = "default-src 'none'; frame-ancestors 'none'; sandbox";

    private readonly ITestOutputHelper _output;

    public SecurityHeadersIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static IEnumerable<object[]> Pages() =>
    [
        ["/"],
        ["packages/TestData"],
        ["packages/TestData/1.2.3"],
        ["packages/Unknown.Package"],
        ["upload"],
        ["stats"],
    ];

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task EveryPage_HasTheHtmlPolicyFrameProtectionAndNoStore(string path)
    {
        using var app = new ElPaquetoApplication(_output);
        await app.AddPackageAsync(TestResources.GetResourceStream(TestResources.Package));
        using var client = app.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        AssertCommonHeaders(response);
        Assert.Equal(HtmlPolicy, Single(response, "Content-Security-Policy"));
        Assert.Equal("DENY", Single(response, "X-Frame-Options"));
        Assert.True(response.Headers.CacheControl?.NoStore, "An HTML page must not be stored by any cache.");
    }

    [Theory]
    [InlineData("v3/index.json")]
    [InlineData("v3/search?q=TestData")]
    [InlineData("v3/registration/testdata/index.json")]
    [InlineData("v3/registration/testdata/1.2.3.json")]
    [InlineData("v3/package/testdata/index.json")]
    [InlineData("v3/package/testdata/1.2.3/testdata.1.2.3.nupkg")]
    [InlineData("v3/package/testdata/1.2.3/testdata.nuspec")]
    public async Task ProtocolResponses_HaveTheBaseHeadersAndStayCacheable(string path)
    {
        using var app = new ElPaquetoApplication(_output);
        await app.AddPackageAsync(TestResources.GetResourceStream(TestResources.Package));
        using var client = app.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertCommonHeaders(response);
        Assert.Equal(ResourcePolicy, Single(response, "Content-Security-Policy"));
        Assert.Equal("DENY", Single(response, "X-Frame-Options"));
        Assert.False(response.Headers.CacheControl?.NoStore ?? false, $"{path} must stay cacheable by the client.");
        Assert.False(response.Headers.CacheControl?.NoCache ?? false, $"{path} must stay cacheable by the client.");
    }

    [Fact]
    public async Task StaticFiles_HaveTheBaseHeadersAndStayCacheable()
    {
        // The test host runs as Production, where static web assets are only served from a publish folder.
        using var app = new ElPaquetoApplication(_output);
        using var withAssets = app.WithWebHostBuilder(builder => builder.UseStaticWebAssets());
        using var client = withAssets.CreateClient();

        using var response = await client.GetAsync("_content/ElPaqueto.Web/js/site.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertCommonHeaders(response);
        Assert.False(response.Headers.CacheControl?.NoStore ?? false);
    }

    [Fact]
    public async Task AnonymousChallenge_IsNotStored()
    {
        using var app = new ElPaquetoApplication(_output);
        using var client = app.CreateAnonymousClient();

        using var response = await client.GetAsync("v3/index.json");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertCommonHeaders(response);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Forbidden_IsNotStored()
    {
        using var app = new ElPaquetoApplication(_output);
        await app.AddUserAsync("nobody", "a long enough passphrase for tests", Permission.None);
        using var client = app.CreateClientFor("nobody", "a long enough passphrase for tests");

        using var response = await client.GetAsync("v3/package/testdata/1.2.3/testdata.1.2.3.nupkg");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertCommonHeaders(response);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task NotFound_IsNotStored()
    {
        using var app = new ElPaquetoApplication(_output);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("v3/package/unknown.package/1.0.0/unknown.package.1.0.0.nupkg");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertCommonHeaders(response);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task HttpsRequiredRefusal_HasTheBaseHeaders()
    {
        using var app = new ElPaquetoApplication(_output);

        var context = await SendAsync(app, "/v3/index.json", "203.0.113.10");

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Hsts_IsOffByDefault_EvenOverHttps()
    {
        using var app = new ElPaquetoApplication(_output);

        var context = await SendAsync(app, "/v3/index.json", "203.0.113.10", scheme: "https", host: "nuget.example.test");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Hsts_WhenEnabled_IsSentOverHttps()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: EnableHsts);

        var context = await SendAsync(app, "/v3/index.json", "203.0.113.10", scheme: "https", host: "nuget.example.test");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("max-age=31536000", context.Response.Headers.StrictTransportSecurity.ToString());
    }

    [Fact]
    public async Task Hsts_WhenEnabled_IsSentOnErrorsAndPagesOverHttpsToo()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: EnableHsts);

        var challenge = await SendAsync(app, "/v3/index.json", "203.0.113.10", scheme: "https", host: "nuget.example.test", anonymous: true);
        var page = await SendAsync(app, "/upload", "203.0.113.10", scheme: "https", host: "nuget.example.test");

        Assert.Equal(StatusCodes.Status401Unauthorized, challenge.Response.StatusCode);
        Assert.Equal("max-age=31536000", challenge.Response.Headers.StrictTransportSecurity.ToString());
        Assert.Equal(StatusCodes.Status200OK, page.Response.StatusCode);
        Assert.Equal("max-age=31536000", page.Response.Headers.StrictTransportSecurity.ToString());
    }

    [Fact]
    public async Task Hsts_WhenEnabled_IsNotSentOverPlainHttp()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: EnableHsts);

        var context = await SendAsync(app, "/v3/index.json", "127.0.0.1", host: "nuget.example.test");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Hsts_WhenEnabled_FollowsTheSchemeReportedByATrustedProxy()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: EnableHsts);

        var context = await SendAsync(app, "/v3/index.json", "127.0.0.1", host: "nuget.example.test", headers: ["X-Forwarded-Proto", "https"]);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("max-age=31536000", context.Response.Headers.StrictTransportSecurity.ToString());
    }

    [Fact]
    public async Task Hsts_WhenEnabled_IgnoresTheSchemeClaimedByAnUntrustedSender()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: dict =>
        {
            EnableHsts(dict);
            dict["Security:Transport:RequireHttps"] = "false";
        });

        var context = await SendAsync(app, "/v3/index.json", "203.0.113.10", host: "nuget.example.test", headers: ["X-Forwarded-Proto", "https"]);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Hsts_WhenEnabled_FollowsThePublicBaseUrl()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: dict =>
        {
            EnableHsts(dict);
            dict["Security:PublicBaseUrl"] = "https://nuget.example.test";
        });

        var context = await SendAsync(app, "/v3/index.json", "127.0.0.1");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("max-age=31536000", context.Response.Headers.StrictTransportSecurity.ToString());
    }

    [Fact]
    public async Task Hsts_WhenEnabled_IsNeverSentForLocalhost()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: EnableHsts);

        var context = await SendAsync(app, "/v3/index.json", "127.0.0.1", scheme: "https", host: "localhost");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Hsts_HonoursMaxAgeAndIncludeSubDomains()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: dict =>
        {
            EnableHsts(dict);
            dict["Security:Hsts:MaxAgeSeconds"] = "600";
            dict["Security:Hsts:IncludeSubDomains"] = "true";
        });

        var context = await SendAsync(app, "/v3/index.json", "203.0.113.10", scheme: "https", host: "nuget.example.test");

        Assert.Equal("max-age=600; includeSubDomains", context.Response.Headers.StrictTransportSecurity.ToString());
    }

    private static void EnableHsts(Dictionary<string, string> dict)
    {
        dict["Security:Hsts:Enabled"] = "true";
    }

    private static void AssertCommonHeaders(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", Single(response, "Referrer-Policy"));
        Assert.False(response.Headers.Contains("Strict-Transport-Security"), "HSTS is off by default and the test server speaks HTTP.");
    }

    private static string Single(HttpResponseMessage response, string header)
    {
        var values = response.Headers.TryGetValues(header, out var found) ? found.ToList() : [];
        Assert.True(values.Count == 1, $"Expected exactly one {header} header, got {values.Count}.");
        return values[0];
    }

    private static Task<HttpContext> SendAsync(
        WebApplicationFactory<Startup> app,
        string path,
        string clientAddress,
        string scheme = "http",
        string host = "localhost",
        string[] headers = null,
        bool anonymous = false)
    {
        return app.Server.SendAsync(context =>
        {
            context.Request.Method = "GET";
            context.Request.Scheme = scheme;
            context.Request.Host = new HostString(host);
            context.Request.Path = path;
            context.Connection.RemoteIpAddress = IPAddress.Parse(clientAddress);

            headers ??= [];
            for (var i = 0; i < headers.Length; i += 2)
            {
                context.Request.Headers[headers[i]] = headers[i + 1];
            }

            if (!anonymous && app is ElPaquetoApplication testApp)
            {
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{testApp.TestUserName}:{testApp.TestPassword}"));
                context.Request.Headers.Authorization = "Basic " + credentials;
            }
        });
    }
}
