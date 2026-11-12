using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElPaqueto.Security.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// Sign-in throttling (#18) through the whole server pipeline, with a controlled clock.
/// </summary>
/// <remarks>
/// Requests are sent with <c>TestServer.SendAsync</c> so that each test sets the connection's client address,
/// as Kestrel would. 203.0.113.0/24 and 198.51.100.0/24 are documentation ranges.
/// </remarks>
public class ThrottlingIntegrationTests
{
    private const string ClientA = "203.0.113.10";
    private const string ClientB = "198.51.100.20";
    private const string Challenge = "Basic realm=\"ElPaqueto\"";

    private readonly ITestOutputHelper _output;

    public ThrottlingIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task AfterFiveWrongPasswords_TheRightPasswordGets429WithoutVerification_OtherClientsAreUnaffected_AndTheThrottleExpires()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var counter = new CountingPasswordHasher();
        using var baseApp = new ElPaquetoApplication(_output);
        using var app = WithServices(baseApp, time, counter);
        var wrong = Basic(baseApp.TestUserName, baseApp.TestPassword + "-wrong");
        var right = Basic(baseApp.TestUserName, baseApp.TestPassword);

        for (var i = 0; i < 5; i++)
        {
            var failure = await SendAsync(app, ClientA, wrong);
            Assert.Equal(StatusCodes.Status401Unauthorized, failure.Response.StatusCode);
        }

        Assert.Equal(5, counter.VerifyCalls);

        var throttled = await SendAsync(app, ClientA, right);
        AssertThrottled(throttled, expectedRetryAfter: "60");
        Assert.Equal(5, counter.VerifyCalls);

        var otherClient = await SendAsync(app, ClientB, right);
        Assert.Equal(StatusCodes.Status200OK, otherClient.Response.StatusCode);

        time.Advance(TimeSpan.FromSeconds(30));
        AssertThrottled(await SendAsync(app, ClientA, right), expectedRetryAfter: "30");

        time.Advance(TimeSpan.FromSeconds(30));
        var afterExpiry = await SendAsync(app, ClientA, right);
        Assert.Equal(StatusCodes.Status200OK, afterExpiry.Response.StatusCode);
    }

    [Fact]
    public async Task KnownAndUnknownUserNames_GetIdenticalAnswersAtEveryStep()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var baseApp = new ElPaquetoApplication(_output);
        using var app = WithServices(baseApp, time);
        await baseApp.AddUserAsync("disabled-user", "a perfectly valid passphrase", Security.Permission.Read, isActive: false);

        var known = await Sequence(app, ClientA, Basic(baseApp.TestUserName, "wrong password 1234"));
        var unknown = await Sequence(app, ClientB, Basic("nobody-here", "wrong password 1234"));
        var disabled = await Sequence(app, "192.0.2.77", Basic("disabled-user", "a perfectly valid passphrase"));

        Assert.Equal(known, unknown);
        Assert.Equal(known, disabled);
        Assert.Equal(5, known.Count(step => step.StartsWith("401|", StringComparison.Ordinal)));
        Assert.Equal(2, known.Count(step => step.StartsWith("429|", StringComparison.Ordinal)));

        static async Task<List<string>> Sequence(WebApplicationFactory<Startup> app, string client, string credentials)
        {
            var steps = new List<string>();
            for (var i = 0; i < 7; i++)
            {
                steps.Add(Fingerprint(await SendAsync(app, client, credentials)));
            }

            return steps;
        }
    }

    [Fact]
    public async Task ThirtyFailuresAcrossUserNames_ThrottleTheClientEvenForAValidAccount()
    {
        using var baseApp = new ElPaquetoApplication(_output);
        using var app = WithServices(baseApp, new FakeTimeProvider(DateTimeOffset.UtcNow));

        for (var i = 0; i < 30; i++)
        {
            var failure = await SendAsync(app, ClientA, Basic($"guess-{i}", "wrong password 1234"));
            Assert.Equal(StatusCodes.Status401Unauthorized, failure.Response.StatusCode);
        }

        AssertThrottled(await SendAsync(app, ClientA, Basic(baseApp.TestUserName, baseApp.TestPassword)), expectedRetryAfter: "60");
        Assert.Equal(StatusCodes.Status200OK, (await SendAsync(app, ClientB, Basic(baseApp.TestUserName, baseApp.TestPassword))).Response.StatusCode);
    }

    [Fact]
    public async Task ASpoofedForwardedForFromAnUntrustedClient_DoesNotChangeTheThrottledAddress()
    {
        using var baseApp = new ElPaquetoApplication(_output);
        using var app = WithServices(baseApp, new FakeTimeProvider(DateTimeOffset.UtcNow));
        var wrong = Basic(baseApp.TestUserName, "wrong password 1234");

        // Each attempt claims to come from another address: the socket address is what counts.
        for (var i = 0; i < 5; i++)
        {
            var failure = await SendAsync(app, ClientA, wrong, "X-Forwarded-For", $"198.51.100.{i + 1}");
            Assert.Equal(StatusCodes.Status401Unauthorized, failure.Response.StatusCode);
            Assert.Equal(IPAddress.Parse(ClientA), failure.Connection.RemoteIpAddress);
        }

        AssertThrottled(await SendAsync(app, ClientA, wrong, "X-Forwarded-For", "198.51.100.99"), expectedRetryAfter: "60");
    }

    [Fact]
    public async Task ATrustedProxy_IsThrottledPerForwardedClient()
    {
        using var baseApp = new ElPaquetoApplication(_output);
        using var app = WithServices(baseApp, new FakeTimeProvider(DateTimeOffset.UtcNow));
        var wrong = Basic(baseApp.TestUserName, "wrong password 1234");

        // The loopback proxy is trusted by default: the client behind it is the forwarded address.
        for (var i = 0; i < 5; i++)
        {
            await SendAsync(app, "127.0.0.1", wrong, "X-Forwarded-For", ClientA, "X-Forwarded-Proto", "https");
        }

        AssertThrottled(await SendAsync(app, "127.0.0.1", wrong, "X-Forwarded-For", ClientA, "X-Forwarded-Proto", "https"), expectedRetryAfter: "60");
        var otherClient = await SendAsync(app, "127.0.0.1", Basic(baseApp.TestUserName, baseApp.TestPassword), "X-Forwarded-For", ClientB, "X-Forwarded-Proto", "https");
        Assert.Equal(StatusCodes.Status200OK, otherClient.Response.StatusCode);
    }

    [Fact]
    public async Task WhenVerificationCapacityIsExhausted_TheAnswerIs503WithRetryAfter()
    {
        var hasher = new BlockingPasswordHasher();
        using var baseApp = new ElPaquetoApplication(_output, inMemoryConfiguration: dict =>
        {
            dict["Security:Throttling:MaxConcurrentVerifications"] = "1";
            dict["Security:Throttling:VerificationQueueLimit"] = "0";
        });
        using var app = WithServices(baseApp, new FakeTimeProvider(DateTimeOffset.UtcNow), hasher);

        try
        {
            // The test server may run the pipeline synchronously up to the blocking verification.
            var blocked = Task.Run(() => SendAsync(app, ClientA, Basic(baseApp.TestUserName, baseApp.TestPassword)));
            Assert.True(hasher.Entered.Wait(TimeSpan.FromSeconds(30)), "The first verification did not start.");

            var busy = await SendAsync(app, ClientB, Basic("someone", "another password 1234"));

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, busy.Response.StatusCode);
            Assert.Equal("5", busy.Response.Headers.RetryAfter.ToString());
            Assert.False(busy.Response.Headers.ContainsKey("WWW-Authenticate"));

            hasher.Release.Set();
            Assert.Equal(StatusCodes.Status200OK, (await blocked).Response.StatusCode);
        }
        finally
        {
            hasher.Release.Set();
        }
    }

    [Fact]
    public async Task ATrip_IsLoggedOnceAndNoLogContainsTheCredentials()
    {
        var logs = new List<(EventId Id, string Message)>();
        using var baseApp = new ElPaquetoApplication(_output);
        using var app = WithServices(baseApp, new FakeTimeProvider(DateTimeOffset.UtcNow)).WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(new CapturingLoggerProvider(logs));
        }));
        const string password = "wrong-password-marker-1234";
        var credentials = Basic("user-name-marker", password);

        for (var i = 0; i < 10; i++)
        {
            await SendAsync(app, ClientA, credentials);
        }

        (EventId Id, string Message)[] captured;
        lock (logs)
        {
            captured = logs.ToArray();
        }

        var trip = Assert.Single(captured, entry => entry.Id.Id == 1801);
        Assert.Contains(ClientA, trip.Message, StringComparison.Ordinal);
        Assert.Contains("Pair", trip.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(captured, entry => entry.Message.Contains(password, StringComparison.Ordinal));
        Assert.DoesNotContain(captured, entry => entry.Message.Contains(credentials, StringComparison.Ordinal));

        // The trip never names the user; only the sign-in failures do, by design of the audit trail (#22).
        Assert.DoesNotContain("user-name-marker", trip.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(captured, entry => entry.Id.Id != 1002 && entry.Message.Contains("user-name-marker", StringComparison.Ordinal));
    }

    private static WebApplicationFactory<Startup> WithServices(
        ElPaquetoApplication app,
        TimeProvider time,
        IPasswordHasher<User> hasher = null)
    {
        return app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(time);
            if (hasher is not null)
            {
                services.RemoveAll<IPasswordHasher<User>>();
                services.AddSingleton(hasher);
            }
        }));
    }

    private static void AssertThrottled(HttpContext context, string expectedRetryAfter)
    {
        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.Equal(expectedRetryAfter, context.Response.Headers.RetryAfter.ToString());
        Assert.False(context.Response.Headers.ContainsKey("WWW-Authenticate"));
        Assert.Equal(string.Empty, ReadBody(context));
    }

    // Status, every header except Date, and the body: what a client can observe.
    private static string Fingerprint(HttpContext context)
    {
        var headers = context.Response.Headers
            .Where(h => !h.Key.Equals("Date", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => $"{h.Key}: {h.Value}");

        return $"{context.Response.StatusCode}|{string.Join("|", headers)}|{ReadBody(context)}";
    }

    private static string Basic(string userName, string password)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));

    private static Task<HttpContext> SendAsync(WebApplicationFactory<Startup> app, string clientAddress, string authorization, params string[] headers)
    {
        return app.Server.SendAsync(context =>
        {
            context.Request.Method = "GET";
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("localhost");
            context.Request.Path = "/v3/index.json";
            context.Connection.RemoteIpAddress = IPAddress.Parse(clientAddress);
            context.Request.Headers.Authorization = authorization;

            for (var i = 0; i < headers.Length; i += 2)
            {
                context.Request.Headers[headers[i]] = headers[i + 1];
            }
        });
    }

    private static string ReadBody(HttpContext context)
    {
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private class CountingPasswordHasher : IPasswordHasher<User>
    {
        private readonly PasswordHasher<User> _inner = new(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = 100_000,
        }));

        private int _verifyCalls;

        public int VerifyCalls => Volatile.Read(ref _verifyCalls);

        public string HashPassword(User user, string password) => _inner.HashPassword(user, password);

        public virtual PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref _verifyCalls);
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    private sealed class BlockingPasswordHasher : CountingPasswordHasher
    {
        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public override PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(60));
            return base.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    private sealed class CapturingLoggerProvider(List<(EventId Id, string Message)> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(logs);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<(EventId Id, string Message)> logs) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (logs)
                {
                    logs.Add((eventId, formatter(state, exception) + (exception == null ? string.Empty : " " + exception)));
                }
            }
        }
    }
}
