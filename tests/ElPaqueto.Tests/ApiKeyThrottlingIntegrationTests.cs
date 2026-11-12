using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElPaqueto.Security;
using ElPaqueto.Security.ApiKeys;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// Sign-in throttling (#18) applied to API keys (#32, #34): keys have no user name, so their attempts count
/// against the client only, and the throttle is consulted before the key is validated.
/// </summary>
/// <remarks>
/// Requests are sent with <c>TestServer.SendAsync</c> so that each test sets the connection's client address.
/// 203.0.113.0/24 and 198.51.100.0/24 are documentation ranges. The client limit is lowered to 3 failures.
/// </remarks>
public class ApiKeyThrottlingIntegrationTests
{
    private const string ClientA = "203.0.113.10";
    private const string ClientB = "198.51.100.20";
    private const string ClientLimit = "3";

    private readonly ITestOutputHelper _output;

    public ApiKeyThrottlingIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AfterTheClientLimit_TheRightKeyGets429WithoutValidation_AndOtherClientsAreUnaffected(bool inHeader)
    {
        using var baseApp = NewApp();
        var counter = new ValidationCounter();
        using var app = WithServices(baseApp, new FakeTimeProvider(DateTimeOffset.UtcNow), counter);
        var wrong = WrongKey(baseApp.TestApiKey);

        for (var i = 0; i < 3; i++)
        {
            var failure = await SendAsync(app, ClientA, wrong, inHeader);
            Assert.Equal(StatusCodes.Status401Unauthorized, failure.Response.StatusCode);
        }

        Assert.Equal(3, counter.Calls);

        var throttled = await SendAsync(app, ClientA, baseApp.TestApiKey, inHeader);
        AssertThrottled(throttled);
        Assert.Equal(3, counter.Calls);

        // The other way of sending a key, and a password, are refused too: the client is throttled, whatever it sends.
        AssertThrottled(await SendAsync(app, ClientA, baseApp.TestApiKey, !inHeader));
        AssertThrottled(await SendAsync(app, ClientA, authorization: Basic(baseApp.TestUserName, baseApp.TestPassword)));
        Assert.Equal(3, counter.Calls);

        var otherClient = await SendAsync(app, ClientB, baseApp.TestApiKey, inHeader);
        Assert.Equal(StatusCodes.Status200OK, otherClient.Response.StatusCode);
    }

    [Fact]
    public async Task KeyFailures_CountWithPasswordFailuresAgainstTheClient()
    {
        using var baseApp = NewApp();
        using var app = WithServices(baseApp, new FakeTimeProvider(DateTimeOffset.UtcNow));

        Assert.Equal(StatusCodes.Status401Unauthorized, (await SendAsync(app, ClientA, WrongKey(baseApp.TestApiKey), inHeader: true)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, (await SendAsync(app, ClientA, authorization: Basic(baseApp.TestUserName, "wrong password 1234"))).Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, (await SendAsync(app, ClientA, WrongKey(baseApp.TestApiKey), inHeader: false)).Response.StatusCode);

        AssertThrottled(await SendAsync(app, ClientA, baseApp.TestApiKey, inHeader: true));
    }

    [Fact]
    public async Task ASuccessfulKey_ResetsNoClientCounter()
    {
        using var baseApp = NewApp();
        using var app = WithServices(baseApp, new FakeTimeProvider(DateTimeOffset.UtcNow));
        var wrong = WrongKey(baseApp.TestApiKey);

        Assert.Equal(StatusCodes.Status401Unauthorized, (await SendAsync(app, ClientA, wrong, inHeader: true)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, (await SendAsync(app, ClientA, wrong, inHeader: true)).Response.StatusCode);

        // A valid key, from the same client and from another one, while client A has two failures.
        Assert.Equal(StatusCodes.Status200OK, (await SendAsync(app, ClientA, baseApp.TestApiKey, inHeader: true)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await SendAsync(app, ClientB, baseApp.TestApiKey, inHeader: true)).Response.StatusCode);

        // Neither success refilled client A's budget: its third failure trips the throttle.
        Assert.Equal(StatusCodes.Status401Unauthorized, (await SendAsync(app, ClientA, wrong, inHeader: true)).Response.StatusCode);
        AssertThrottled(await SendAsync(app, ClientA, baseApp.TestApiKey, inHeader: true));
        Assert.Equal(StatusCodes.Status200OK, (await SendAsync(app, ClientB, baseApp.TestApiKey, inHeader: true)).Response.StatusCode);
    }

    private ElPaquetoApplication NewApp()
        => new(_output, inMemoryConfiguration: dict => dict["Security:Throttling:ClientFailureLimit"] = ClientLimit);

    // Same key id, another secret: a well-formed key that fails validation.
    private static string WrongKey(string token) => token[..^1] + (token[^1] == 'a' ? 'b' : 'a');

    private static WebApplicationFactory<Startup> WithServices(ElPaquetoApplication app, TimeProvider time, ValidationCounter counter = null)
    {
        return app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(time);
            if (counter is not null)
            {
                services.RemoveAll<IApiKeyService>();
                services.AddScoped<IApiKeyService>(provider => new CountingApiKeyService(
                    ActivatorUtilities.CreateInstance<ApiKeyService>(provider),
                    counter));
            }
        }));
    }

    private static void AssertThrottled(HttpContext context)
    {
        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.Equal("60", context.Response.Headers.RetryAfter.ToString());
        Assert.False(context.Response.Headers.ContainsKey("WWW-Authenticate"));
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        Assert.Equal(string.Empty, reader.ReadToEnd());
    }

    private static string Basic(string userName, string password)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));

    private static Task<HttpContext> SendAsync(WebApplicationFactory<Startup> app, string clientAddress, string token, bool inHeader)
        => inHeader
            ? SendAsync(app, clientAddress, apiKey: token)
            : SendAsync(app, clientAddress, authorization: Basic("apikey", token));

    private static Task<HttpContext> SendAsync(WebApplicationFactory<Startup> app, string clientAddress, string apiKey = null, string authorization = null)
    {
        return app.Server.SendAsync(context =>
        {
            context.Request.Method = "GET";
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("localhost");
            context.Request.Path = "/v3/index.json";
            context.Connection.RemoteIpAddress = IPAddress.Parse(clientAddress);
            if (apiKey is not null)
            {
                context.Request.Headers[TestUserExtensions.ApiKeyHeader] = apiKey;
            }

            if (authorization is not null)
            {
                context.Request.Headers.Authorization = authorization;
            }
        });
    }

    private sealed class ValidationCounter
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Increment() => Interlocked.Increment(ref _calls);
    }

    private sealed class CountingApiKeyService(IApiKeyService inner, ValidationCounter counter) : IApiKeyService
    {
        public Task<ApiKeyCreated> CreateAsync(Guid ownerUserId, string name, Permission permissions, DateTime? expiresAt, CancellationToken cancellationToken = default)
            => inner.CreateAsync(ownerUserId, name, permissions, expiresAt, cancellationToken);

        public Task<System.Collections.Generic.IReadOnlyList<ApiKeyInfo>> ListAsync(Guid? ownerUserId = null, CancellationToken cancellationToken = default)
            => inner.ListAsync(ownerUserId, cancellationToken);

        public Task<ApiKeyInfo> FindAsync(string keyId, CancellationToken cancellationToken = default)
            => inner.FindAsync(keyId, cancellationToken);

        public Task<ApiKeyRevocation> RevokeAsync(string keyId, CancellationToken cancellationToken = default)
            => inner.RevokeAsync(keyId, cancellationToken);

        public Task<ApiKeyCreated> RotateAsync(string keyId, DateTime? expiresAt, CancellationToken cancellationToken = default)
            => inner.RotateAsync(keyId, expiresAt, cancellationToken);

        public Task<ApiKeyValidationResult> ValidateAsync(string token, CancellationToken cancellationToken = default)
        {
            counter.Increment();
            return inner.ValidateAsync(token, cancellationToken);
        }
    }
}
