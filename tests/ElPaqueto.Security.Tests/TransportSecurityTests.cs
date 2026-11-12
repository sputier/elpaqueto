using System.Net;
using ElPaqueto.Core;
using ElPaqueto.Security.Configuration;
using ElPaqueto.Security.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace ElPaqueto.Security.Tests;

public class TransportSecurityTests
{
    private static readonly ValidateElPaquetoOptions<ElPaquetoSecurityOptions> Validator = new("Security");

    [Fact]
    public void Defaults_RequireHttpsTrustLoopbackOnlyAndHaveNoPublicBaseUrl()
    {
        var options = new ElPaquetoSecurityOptions();

        Assert.True(options.Transport.RequireHttps);
        Assert.Empty(options.ReverseProxy.KnownProxies);
        Assert.Empty(options.ReverseProxy.KnownNetworks);
        Assert.Equal(1, options.ReverseProxy.ForwardLimit);
        Assert.Null(options.PublicBaseUrl);
        Assert.True(Validator.Validate(name: null, options).Succeeded);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData(" 192.168.1.10 ")]
    public void ValidProxy_IsAccepted(string proxy)
    {
        var options = WithProxy(proxies: [proxy]);

        Assert.True(Validator.Validate(name: null, options).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("example.test")]
    [InlineData("10.0.0.0/8")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("256.0.0.1")]
    public void InvalidProxy_IsRejected(string proxy)
    {
        var result = Validator.Validate(name: null, WithProxy(proxies: [proxy]));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Security:ReverseProxy:KnownProxies:0", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("172.18.0.0/16")]
    [InlineData("192.168.1.10/32")]
    [InlineData("fd00::/8")]
    [InlineData("::1/128")]
    [InlineData("::ffff:10.0.0.0/104")]
    public void ValidNetwork_IsAccepted(string network)
    {
        Assert.True(Validator.Validate(name: null, WithProxy(networks: [network])).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("10.0.0.0")]
    [InlineData("10/8")]
    [InlineData("10.0.0.1/8")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("example.test/8")]
    public void MalformedNetwork_IsRejected(string network)
    {
        var result = Validator.Validate(name: null, WithProxy(networks: [network]));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Security:ReverseProxy:KnownNetworks:0 is not a valid", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("::ffff:0:0/96")]
    [InlineData("::/64")]
    [InlineData("::/80")]
    public void CatchAllNetwork_IsRejected(string network)
    {
        var result = Validator.Validate(name: null, WithProxy(networks: ["10.0.0.0/8", network]));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Security:ReverseProxy:KnownNetworks:1 trusts every address", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public void ForwardLimitOutsideTheAcceptedRange_IsRejected(int limit)
    {
        var options = new ElPaquetoSecurityOptions { ReverseProxy = new SecurityReverseProxyOptions { ForwardLimit = limit } };

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Security:ReverseProxy:ForwardLimit", StringComparison.Ordinal));
    }

    [Fact]
    public void NullLists_AreRejected()
    {
        var options = new ElPaquetoSecurityOptions { ReverseProxy = new SecurityReverseProxyOptions { KnownProxies = null! } };

        Assert.True(Validator.Validate(name: null, options).Failed);
    }

    [Theory]
    [InlineData("https://nuget.example.test")]
    [InlineData("https://nuget.example.test/")]
    [InlineData("https://nuget.example.test:8443")]
    [InlineData("https://192.0.2.10")]
    [InlineData("")]
    [InlineData("  ")]
    public void ValidOrEmptyPublicBaseUrl_IsAccepted(string url)
    {
        var options = new ElPaquetoSecurityOptions { PublicBaseUrl = url };

        Assert.True(Validator.Validate(name: null, options).Succeeded);
    }

    [Theory]
    [InlineData("http://nuget.example.test")]
    [InlineData("nuget.example.test")]
    [InlineData("/v3/index.json")]
    [InlineData("ftp://nuget.example.test")]
    [InlineData("https://nuget.example.test/feed")]
    [InlineData("https://nuget.example.test/?a=b")]
    [InlineData("https://nuget.example.test/?")]
    [InlineData("https://nuget.example.test/#top")]
    [InlineData("https://user:secret@nuget.example.test")]
    public void InvalidPublicBaseUrl_IsRejected(string url)
    {
        var result = Validator.Validate(name: null, new ElPaquetoSecurityOptions { PublicBaseUrl = url });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Security:PublicBaseUrl", StringComparison.Ordinal));
        Assert.All(result.Failures!, f => Assert.DoesNotContain("secret", f, StringComparison.Ordinal));
    }

    [Fact]
    public void ForwardedHeaders_DefaultTrustsLoopbackOnly()
    {
        var forwarded = Configure(new ElPaquetoSecurityOptions());

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost, forwarded.ForwardedHeaders);
        Assert.Equal(1, forwarded.ForwardLimit);
        Assert.Equal([IPAddress.IPv6Loopback], forwarded.KnownProxies);
        Assert.Equal([new IPNetwork(IPAddress.Loopback, 8)], forwarded.KnownIPNetworks);
    }

    [Fact]
    public void ForwardedHeaders_ConfiguredProxiesReplaceTheDefaultAndEarlierConfiguration()
    {
        var forwarded = new ForwardedHeadersOptions();
        forwarded.KnownProxies.Add(IPAddress.Parse("198.51.100.1"));
        forwarded.KnownIPNetworks.Add(IPNetwork.Parse("198.51.100.0/24"));
#pragma warning disable ASPDEPR005
        forwarded.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("198.51.100.0"), 24));
#pragma warning restore ASPDEPR005
        forwarded.AllowedHosts.Add("*");

        Configure(
            new ElPaquetoSecurityOptions
            {
                PublicBaseUrl = "https://nuget.example.test",
                ReverseProxy = new SecurityReverseProxyOptions
                {
                    KnownProxies = ["10.0.0.5"],
                    KnownNetworks = ["172.18.0.0/16"],
                    ForwardLimit = 2,
                },
            },
            forwarded);

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, forwarded.ForwardedHeaders);
        Assert.Equal(2, forwarded.ForwardLimit);
        Assert.Equal([IPAddress.Parse("10.0.0.5")], forwarded.KnownProxies);
        Assert.Equal([IPNetwork.Parse("172.18.0.0/16")], forwarded.KnownIPNetworks);
#pragma warning disable ASPDEPR005
        Assert.DoesNotContain(forwarded.KnownNetworks, n => n.Prefix.Equals(IPAddress.Parse("198.51.100.0")));
#pragma warning restore ASPDEPR005
        Assert.Empty(forwarded.AllowedHosts);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.200.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("fe80::1", false)]
    public void IsLocal_AcceptsLoopbackOnly(string address, bool expected)
    {
        Assert.Equal(expected, TransportSecurityMiddleware.IsLocal(IPAddress.Parse(address)));
    }

    [Fact]
    public void IsLocal_TransportWithoutAddressIsLocal()
    {
        Assert.True(TransportSecurityMiddleware.IsLocal(null));
    }

    [Theory]
    [InlineData(new[] { "http://localhost:5000" }, true)]
    [InlineData(new[] { "http://127.0.0.1:5000", "http://[::1]:5000" }, true)]
    [InlineData(new[] { "http://unix:/tmp/elpaqueto.sock" }, true)]
    [InlineData(new[] { "http://127.0.0.1:5000", "http://[::]:8080" }, false)]
    [InlineData(new[] { "http://+:8080" }, false)]
    [InlineData(new[] { "http://*:8080" }, false)]
    [InlineData(new[] { "http://0.0.0.0:8080" }, false)]
    [InlineData(new[] { "http://nuget.example.test:8080" }, false)]
    [InlineData(new[] { "not an address" }, false)]
    public void AreLoopbackOnly_ClassifiesBoundAddresses(string[] addresses, bool expected)
    {
        Assert.Equal(expected, TransportStartupWarnings.AreLoopbackOnly(addresses));
    }

    [Fact]
    public async Task StartupWarnings_HttpsNotRequired_IsLogged()
    {
        var logger = new ListLogger<TransportStartupWarnings>();
        var warnings = CreateWarnings(new ElPaquetoSecurityOptions { Transport = new SecurityTransportOptions { RequireHttps = false } }, logger, ["http://localhost:5000"]);

        await warnings.StartingAsync(CancellationToken.None);
        await warnings.StartedAsync(CancellationToken.None);

        var message = Assert.Single(logger.Messages);
        Assert.Contains("Security:Transport:RequireHttps is false", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupWarnings_NoPublicBaseUrlBeyondLoopback_IsLogged()
    {
        var logger = new ListLogger<TransportStartupWarnings>();
        var warnings = CreateWarnings(new ElPaquetoSecurityOptions(), logger, ["http://[::]:8080"]);

        await warnings.StartingAsync(CancellationToken.None);
        await warnings.StartedAsync(CancellationToken.None);

        var message = Assert.Single(logger.Messages);
        Assert.Contains("Security:PublicBaseUrl is not set", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://nuget.example.test", "http://[::]:8080")]
    [InlineData(null, "http://127.0.0.1:5000")]
    public async Task StartupWarnings_SafeConfiguration_LogsNothing(string? publicBaseUrl, string address)
    {
        var logger = new ListLogger<TransportStartupWarnings>();
        var warnings = CreateWarnings(new ElPaquetoSecurityOptions { PublicBaseUrl = publicBaseUrl }, logger, [address]);

        await warnings.StartingAsync(CancellationToken.None);
        await warnings.StartedAsync(CancellationToken.None);

        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task Middleware_RemotePlainHttp_GetsAPlainTextForbiddenWithoutCallingTheNextMiddleware()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(new ElPaquetoSecurityOptions(), _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext("http", "203.0.113.10");
        context.Request.Headers.Authorization = "Basic dXNlcjpwYXNz";

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.StartsWith("text/plain", context.Response.ContentType, StringComparison.Ordinal);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.False(context.Response.Headers.ContainsKey("WWW-Authenticate"));
        Assert.False(context.Response.Headers.ContainsKey("Location"));
        Assert.Equal(TransportSecurityMiddleware.HttpsRequiredMessage, ReadBody(context));
    }

    [Fact]
    public async Task Middleware_PublicBaseUrl_PinsSchemeAndHost()
    {
        HttpRequest? seen = null;
        var middleware = CreateMiddleware(
            new ElPaquetoSecurityOptions { PublicBaseUrl = "https://nuget.example.test:8443/" },
            context =>
            {
                seen = context.Request;
                return Task.CompletedTask;
            });
        var context = CreateContext("http", "127.0.0.1");
        context.Request.Host = new HostString("attacker.example.com");

        await middleware.InvokeAsync(context);

        Assert.NotNull(seen);
        Assert.Equal("https", seen.Scheme);
        Assert.Equal("nuget.example.test:8443", seen.Host.Value);
    }

    [Fact]
    public async Task Middleware_RemoteHttps_IsPassedThroughUnchanged()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(new ElPaquetoSecurityOptions(), _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext("https", "203.0.113.10");
        context.Request.Host = new HostString("nuget.example.test");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal("nuget.example.test", context.Request.Host.Value);
    }

    private static ElPaquetoSecurityOptions WithProxy(string[]? proxies = null, string[]? networks = null)
    {
        return new ElPaquetoSecurityOptions
        {
            ReverseProxy = new SecurityReverseProxyOptions
            {
                KnownProxies = proxies ?? [],
                KnownNetworks = networks ?? [],
            },
        };
    }

    private static ForwardedHeadersOptions Configure(ElPaquetoSecurityOptions security, ForwardedHeadersOptions? forwarded = null)
    {
        forwarded ??= new ForwardedHeadersOptions();
        new ConfigureTrustedForwardedHeaders(Options.Create(security)).PostConfigure(Options.DefaultName, forwarded);
        return forwarded;
    }

    private static TransportStartupWarnings CreateWarnings(
        ElPaquetoSecurityOptions options,
        ILogger<TransportStartupWarnings> logger,
        string[] addresses)
    {
        var services = new ServiceCollection()
            .AddSingleton<IServer>(new FakeServer(addresses))
            .BuildServiceProvider();

        return new TransportStartupWarnings(Options.Create(options), services, logger);
    }

    private static TransportSecurityMiddleware CreateMiddleware(ElPaquetoSecurityOptions options, RequestDelegate next)
    {
        return new TransportSecurityMiddleware(next, Options.Create(options), NullLogger<TransportSecurityMiddleware>.Instance);
    }

    private static DefaultHttpContext CreateContext(string scheme, string remoteAddress)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString("localhost");
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return reader.ReadToEnd();
    }

    private sealed class FakeServer : IServer
    {
        public FakeServer(string[] addresses)
        {
            var feature = new ServerAddressesFeature();
            foreach (var address in addresses)
            {
                feature.Addresses.Add(address);
            }

            Features.Set<IServerAddressesFeature>(feature);
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();

        public void Dispose()
        {
        }

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ServerAddressesFeature : IServerAddressesFeature
    {
        public ICollection<string> Addresses { get; } = [];

        public bool PreferHostingUrls { get; set; }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _messages.Add(formatter(state, exception));
        }
    }
}
