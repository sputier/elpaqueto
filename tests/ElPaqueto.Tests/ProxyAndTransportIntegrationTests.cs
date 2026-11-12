using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NuGet.Versioning;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// Forwarded headers, public base URL and HTTPS policy (#19), through the whole server pipeline.
/// </summary>
/// <remarks>
/// Requests are sent with <c>TestServer.SendAsync</c> so that each test sets the connection's
/// client address and scheme, as Kestrel would for a remote client or a TLS connection.
/// 203.0.113.0/24 and 198.51.100.0/24 are documentation ranges that stand for remote clients; 192.0.2.0/24 stands for the proxies' network.
/// </remarks>
public class ProxyAndTransportIntegrationTests
{
    private const string RemoteClient = "203.0.113.10";
    private const string OtherRemoteClient = "198.51.100.20";
    private const string ProxyInPrivateNetwork = "192.0.2.3";
    private const string Attacker = "attacker.example.com";

    private static readonly string[] SpoofingHeaders =
    {
        "X-Forwarded-Host", Attacker,
        "X-Forwarded-Proto", "https",
        "X-Forwarded-For", "127.0.0.1",
    };

    private readonly ITestOutputHelper _output;

    public ProxyAndTransportIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task UntrustedRemote_SpoofedProtoAndHost_IsTreatedAsPlainHttpAndRejected()
    {
        using var app = new ElPaquetoApplication(_output);

        var context = await SendAsync(app, "/v3/index.json", RemoteClient, headers: SpoofingHeaders);

        AssertHttpsRequired(context);
    }

    [Fact]
    public async Task UntrustedRemote_SpoofedHeaders_DoNotChangeUrlsOrClientAddress()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: NoHttpsRequirement);

        var context = await SendAsync(app, "/v3/index.json", RemoteClient, headers: SpoofingHeaders);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(IPAddress.Parse(RemoteClient), context.Connection.RemoteIpAddress);
        AssertAllServerUrlsStartWith("http://localhost/", ReadBody(context));
    }

    [Fact]
    public async Task UntrustedRemote_OverHttps_SpoofedHostIsIgnored()
    {
        using var app = new ElPaquetoApplication(_output);

        var context = await SendAsync(app, "/v3/index.json", RemoteClient, scheme: "https", headers: SpoofingHeaders);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        AssertAllServerUrlsStartWith("https://localhost/", ReadBody(context));
    }

    [Fact]
    public async Task UntrustedRemote_SpoofedLoopbackForwardedFor_IsNotTreatedAsLoopback()
    {
        using var app = new ElPaquetoApplication(_output);

        var context = await SendAsync(app, "/v3/index.json", RemoteClient, headers: new[] { "X-Forwarded-For", "127.0.0.1" });

        AssertHttpsRequired(context);
        Assert.Equal(IPAddress.Parse(RemoteClient), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task DefaultTrustedLoopbackProxy_ForwardedHttpsIsHonoured()
    {
        using var app = new ElPaquetoApplication(_output);

        var context = await SendAsync(app, "/v3/index.json", "127.0.0.1", headers: new[]
        {
            "X-Forwarded-For", RemoteClient,
            "X-Forwarded-Proto", "https",
            "X-Forwarded-Host", "nuget.example.test",
        });

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(IPAddress.Parse(RemoteClient), context.Connection.RemoteIpAddress);
        AssertAllServerUrlsStartWith("https://nuget.example.test/", ReadBody(context));
    }

    [Fact]
    public async Task DefaultTrustedLoopbackProxy_RelayingRemotePlainHttp_IsRejected()
    {
        using var app = new ElPaquetoApplication(_output);

        var context = await SendAsync(app, "/v3/index.json", "127.0.0.1", headers: new[]
        {
            "X-Forwarded-For", RemoteClient,
            "X-Forwarded-Proto", "http",
        });

        AssertHttpsRequired(context);
    }

    [Fact]
    public async Task ConfiguredProxyNetwork_IsTrusted()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: TrustPrivateNetwork);

        var context = await SendAsync(app, "/v3/index.json", ProxyInPrivateNetwork, headers: new[]
        {
            "X-Forwarded-For", RemoteClient,
            "X-Forwarded-Proto", "https",
            "X-Forwarded-Host", "nuget.example.test",
        });

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(IPAddress.Parse(RemoteClient), context.Connection.RemoteIpAddress);
        AssertAllServerUrlsStartWith("https://nuget.example.test/", ReadBody(context));
    }

    [Fact]
    public async Task ConfiguredProxy_WithoutForwardedHttps_IsRejected()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: TrustPrivateNetwork);

        var context = await SendAsync(app, "/v3/index.json", ProxyInPrivateNetwork, headers: new[] { "X-Forwarded-For", RemoteClient });

        AssertHttpsRequired(context);
    }

    [Fact]
    public async Task ConfiguredProxyNetwork_ReplacesTheLoopbackDefault()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: TrustPrivateNetwork);

        // Loopback is no longer a trusted proxy: its forwarded headers are ignored, and the request
        // stays a local plain-HTTP request.
        var context = await SendAsync(app, "/v3/index.json", "127.0.0.1", headers: SpoofingHeaders.Concat(new[] { "X-Forwarded-For", RemoteClient }).ToArray());

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(IPAddress.Loopback, context.Connection.RemoteIpAddress);
        AssertAllServerUrlsStartWith("http://localhost/", ReadBody(context));
    }

    [Fact]
    public async Task AddressOutsideTheConfiguredProxies_IsNotTrusted()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: TrustPrivateNetwork);

        var context = await SendAsync(app, "/v3/index.json", OtherRemoteClient, headers: SpoofingHeaders);

        AssertHttpsRequired(context);
        Assert.Equal(IPAddress.Parse(OtherRemoteClient), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task ForwardLimit_OnlyTheLastEntriesAreUsed()
    {
        using var app = new ElPaquetoApplication(_output);

        // A client can prepend anything to X-Forwarded-For: with the default limit of 1, only the
        // entry added by the trusted proxy (the last one) is used.
        var context = await SendAsync(app, "/v3/index.json", "127.0.0.1", headers: new[]
        {
            "X-Forwarded-For", $"127.0.0.1, {RemoteClient}",
            "X-Forwarded-Proto", "https, http",
        });

        AssertHttpsRequired(context);
        Assert.Equal(IPAddress.Parse(RemoteClient), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task PublicBaseUrl_PinsEveryGeneratedUrl()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: dict => dict["Security:PublicBaseUrl"] = "https://nuget.example.test");
        await app.AddPackageAsync(TestResources.GetResourceStream(TestResources.Package));

        var headers = new[] { "X-Forwarded-Host", Attacker, "X-Forwarded-Proto", "http" };
        var paths = new[]
        {
            "/v3/index.json",
            "/v3/registration/testdata/index.json",
            "/v3/registration/testdata/1.2.3.json",
            "/v3/search",
        };

        foreach (var path in paths)
        {
            var context = await SendAsync(app, path, "127.0.0.1", host: Attacker, headers: headers);

            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            var body = ReadBody(context);
            Assert.DoesNotContain(Attacker, body, StringComparison.OrdinalIgnoreCase);
            AssertAllServerUrlsStartWith("https://nuget.example.test/", body);
        }

        // The two hand-built resources of the service index, checked by name.
        var index = ReadBody(await SendAsync(app, "/v3/index.json", "127.0.0.1", host: Attacker, headers: headers));
        Assert.Contains("\"https://nuget.example.test/v3/package/\"", index);
        Assert.Contains("\"https://nuget.example.test/v3/registration/\"", index);
    }

    [Fact]
    public async Task PublicBaseUrl_KeepsThePathBase()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: dict =>
        {
            dict["Security:PublicBaseUrl"] = "https://nuget.example.test";
            dict["PathBase"] = "/feed";
        });

        var context = await SendAsync(app, "/feed/v3/index.json", "127.0.0.1", host: Attacker);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        AssertAllServerUrlsStartWith("https://nuget.example.test/feed/", ReadBody(context));
    }

    [Fact]
    public async Task PublicBaseUrl_DoesNotMakeRemotePlainHttpLookSecure()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: dict => dict["Security:PublicBaseUrl"] = "https://nuget.example.test");

        var context = await SendAsync(app, "/v3/index.json", RemoteClient);

        AssertHttpsRequired(context);
    }

    [Fact]
    public async Task AllowedHosts_RejectsAnUnlistedHostHeader()
    {
        // Without a public base URL, the built-in host filtering is the allow-list for Host.
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: dict => dict["AllowedHosts"] = "nuget.example.test");

        var rejected = await SendAsync(app, "/v3/index.json", "127.0.0.1", host: Attacker);
        var accepted = await SendAsync(app, "/v3/index.json", "127.0.0.1", host: "nuget.example.test");

        Assert.Equal(StatusCodes.Status400BadRequest, rejected.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, accepted.Response.StatusCode);
        AssertAllServerUrlsStartWith("http://nuget.example.test/", ReadBody(accepted));
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-NuGet-ApiKey")]
    [InlineData(null)]
    public async Task RemotePlainHttp_IsRejectedBeforeAuthentication(string credentialHeader)
    {
        var counter = new AuthenticationCounter();
        using var app = CreateWithCredentials(counter);

        var context = await SendAsync(app, "/v3/index.json", RemoteClient, headers: CredentialHeaders(credentialHeader));

        AssertHttpsRequired(context);
        Assert.Equal(0, counter.Calls);
    }

    [Fact]
    public async Task RemotePlainHttp_RejectsEveryRoute()
    {
        using var app = new ElPaquetoApplication(_output);

        foreach (var path in new[] { "/", "/health", "/favicon.ico", "/v3/index.json", "/api/v2/package", "/does-not-exist" })
        {
            AssertHttpsRequired(await SendAsync(app, path, RemoteClient));
        }

        AssertHttpsRequired(await SendAsync(app, "/api/v2/package", RemoteClient, method: "PUT", headers: CredentialHeaders("X-NuGet-ApiKey")));
    }

    [Fact]
    public async Task RemoteHttps_WithValidCredentials_IsAuthenticated()
    {
        var counter = new AuthenticationCounter();
        using var app = CreateWithCredentials(counter);

        var context = await SendAsync(app, "/v3/index.json", RemoteClient, scheme: "https", headers: CredentialHeaders("Authorization"));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(counter.Calls > 0);
    }

    [Fact]
    public async Task RemoteHttps_WithoutCredentials_GetsTheUsualChallenge()
    {
        using var app = CreateWithCredentials(new AuthenticationCounter());

        var context = await SendAsync(app, "/v3/index.json", RemoteClient, scheme: "https", anonymous: true);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.20.30")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData(null)]
    public async Task LocalPlainHttp_WithCredentials_IsAllowed(string clientAddress)
    {
        using var app = CreateWithCredentials(new AuthenticationCounter());

        var context = await SendAsync(app, "/v3/index.json", clientAddress, headers: CredentialHeaders("Authorization"));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task LocalPlainHttp_HealthCheckIsAllowed()
    {
        using var app = new ElPaquetoApplication(_output);

        var context = await SendAsync(app, "/health", "127.0.0.1");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task RequireHttpsDisabled_RemotePlainHttpIsAllowed()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: NoHttpsRequirement);

        var context = await SendAsync(app, "/v3/index.json", RemoteClient);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task ManifestDownloadUrl_ResolvesToTheNuspec()
    {
        using var app = new ElPaquetoApplication(_output);
        await app.AddPackageAsync(TestResources.GetResourceStream(TestResources.Package));

        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        accessor.HttpContext.Request.Scheme = "http";
        accessor.HttpContext.Request.Host = new HostString("localhost");
        var generator = new Web.ElPaquetoUrlGenerator(accessor, app.Services.GetRequiredService<LinkGenerator>());

        var url = new Uri(generator.GetPackageManifestDownloadUrl("TestData", NuGetVersion.Parse("1.2.3")));

        Assert.Equal("/v3/package/testdata/1.2.3/testdata.nuspec", url.AbsolutePath);

        var context = await SendAsync(app, url.AbsolutePath, "127.0.0.1");
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("<id>TestData</id>", ReadBody(context));
    }

    private ElPaquetoApplication CreateWithCredentials(AuthenticationCounter counter)
    {
        return new CountingApplication(_output, counter, dict =>
        {
            dict["Authentication:ApiKeys:0:Key"] = "test-api-key";
        });
    }

    private static string[] CredentialHeaders(string credentialHeader)
    {
        return credentialHeader switch
        {
            "Authorization" => Array.Empty<string>(), // added by SendAsync: the seeded test user
            "X-NuGet-ApiKey" => new[] { "X-NuGet-ApiKey", "test-api-key" },
            _ => Array.Empty<string>(),
        };
    }

    private static void TrustPrivateNetwork(Dictionary<string, string> dict)
    {
        dict["Security:ReverseProxy:KnownNetworks:0"] = "192.0.2.0/24";
    }

    private static void NoHttpsRequirement(Dictionary<string, string> dict)
    {
        dict["Security:Transport:RequireHttps"] = "false";
    }

    private static Task<HttpContext> SendAsync(
        WebApplicationFactory<Startup> app,
        string path,
        string clientAddress,
        string scheme = "http",
        string host = "localhost",
        string method = "GET",
        string[] headers = null,
        bool anonymous = false)
    {
        return app.Server.SendAsync(context =>
        {
            context.Request.Method = method;
            context.Request.Scheme = scheme;
            context.Request.Host = new HostString(host);
            context.Request.Path = path;
            context.Connection.RemoteIpAddress = clientAddress is null ? null : IPAddress.Parse(clientAddress);

            headers ??= Array.Empty<string>();
            for (var i = 0; i < headers.Length; i += 2)
            {
                context.Request.Headers[headers[i]] = headers[i + 1];
            }

            // The server denies anonymous callers: authenticate as the test user unless a test checks anonymity.
            if (!anonymous && app is ElPaquetoApplication testApp && !context.Request.Headers.ContainsKey("Authorization"))
            {
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{testApp.TestUserName}:{testApp.TestPassword}"));
                context.Request.Headers["Authorization"] = "Basic " + credentials;
            }
        });
    }

    private static void AssertHttpsRequired(HttpContext context)
    {
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("WWW-Authenticate"));
        Assert.False(context.Response.Headers.ContainsKey("Location"));
        Assert.Equal("HTTPS is required.", ReadBody(context));
    }

    private static string ReadBody(HttpContext context)
    {
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Asserts that every absolute URL of a JSON body that points at this server starts with
    /// <paramref name="prefix"/>. URLs of other sites (JSON-LD vocabularies) are ignored, but an
    /// attacker-supplied host never is.
    /// </summary>
    private static void AssertAllServerUrlsStartWith(string prefix, string json)
    {
        using var document = JsonDocument.Parse(json);
        var urls = new List<string>();
        CollectUrls(document.RootElement, urls);

        var serverUrls = urls.Where(u => !u.StartsWith("http://schema.nuget.org/", StringComparison.Ordinal)
            && !u.StartsWith("https://api.nuget.org/", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(serverUrls);
        Assert.All(serverUrls, url => Assert.StartsWith(prefix, url, StringComparison.Ordinal));
        Assert.DoesNotContain(urls, url => url.Contains(Attacker, StringComparison.OrdinalIgnoreCase));
    }

    private static void CollectUrls(JsonElement element, List<string> urls)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectUrls(property.Value, urls);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectUrls(item, urls);
                }

                break;
            case JsonValueKind.String:
                var value = element.GetString();
                if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    urls.Add(value);
                }

                break;
        }
    }

    private sealed class AuthenticationCounter
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Increment() => Interlocked.Increment(ref _calls);
    }

    private sealed class CountingApplication : ElPaquetoApplication
    {
        private readonly AuthenticationCounter _counter;

        public CountingApplication(ITestOutputHelper output, AuthenticationCounter counter, Action<Dictionary<string, string>> configuration)
            : base(output, inMemoryConfiguration: configuration)
        {
            _counter = counter;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticationService>();
                services.AddScoped<IAuthenticationService>(provider => new CountingAuthenticationService(
                    ActivatorUtilities.CreateInstance<AuthenticationService>(provider),
                    _counter));
            });
        }
    }

    /// <summary>
    /// Counts every authentication attempt, whatever the handler, so the tests can prove that
    /// credentials sent over remote plain HTTP never reach it.
    /// </summary>
    private sealed class CountingAuthenticationService : IAuthenticationService
    {
        private readonly IAuthenticationService _inner;
        private readonly AuthenticationCounter _counter;

        public CountingAuthenticationService(IAuthenticationService inner, AuthenticationCounter counter)
        {
            _inner = inner;
            _counter = counter;
        }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string scheme)
        {
            _counter.Increment();
            return _inner.AuthenticateAsync(context, scheme);
        }

        public Task ChallengeAsync(HttpContext context, string scheme, AuthenticationProperties properties)
            => _inner.ChallengeAsync(context, scheme, properties);

        public Task ForbidAsync(HttpContext context, string scheme, AuthenticationProperties properties)
            => _inner.ForbidAsync(context, scheme, properties);

        public Task SignInAsync(HttpContext context, string scheme, ClaimsPrincipal principal, AuthenticationProperties properties)
            => _inner.SignInAsync(context, scheme, principal, properties);

        public Task SignOutAsync(HttpContext context, string scheme, AuthenticationProperties properties)
            => _inner.SignOutAsync(context, scheme, properties);
    }
}
