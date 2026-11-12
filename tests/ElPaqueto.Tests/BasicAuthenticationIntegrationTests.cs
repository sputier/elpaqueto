using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using ElPaqueto.Security;
using ElPaqueto.Security.Data;
using ElPaqueto.Security.Passwords;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// HTTP Basic authentication against the real host: strict parsing, the same answer for every kind
/// of failure, no anonymous principal anywhere, and the verification cache.
/// </summary>
public class BasicAuthenticationIntegrationTests : IDisposable
{
    private const string Challenge = "Basic realm=\"ElPaqueto\"";

    private readonly ITestOutputHelper _output;
    private readonly ElPaquetoApplication _app;

    public BasicAuthenticationIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _app = new ElPaquetoApplication(output);
    }

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task NoCredentials_GetsAChallengeWithTheExpectedRealmAndNoRedirect()
    {
        using var client = _app.CreateAnonymousClient();

        using var response = await client.GetAsync("v3/index.json");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(new[] { Challenge }, response.Headers.GetValues("WWW-Authenticate"));
        Assert.Null(response.Headers.Location);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ValidCredentialsOfAnActiveUser_AreAccepted()
    {
        using var client = _app.CreateClient();

        using var response = await client.GetAsync("v3/index.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WrongPasswordUnknownUserAndDisabledUser_GetByteIdenticalResponses()
    {
        await AddUserAsync("disabled-user", "a perfectly valid passphrase", isActive: false);

        var wrongPassword = await Fingerprint(Basic(_app.TestUserName, _app.TestPassword + "x"));
        var unknownUser = await Fingerprint(Basic("nobody-here", _app.TestPassword));
        var disabledUser = await Fingerprint(Basic("disabled-user", "a perfectly valid passphrase"));

        Assert.StartsWith("401", wrongPassword, StringComparison.Ordinal);
        Assert.Equal(wrongPassword, unknownUser);
        Assert.Equal(wrongPassword, disabledUser);
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("BASIC")]
    [InlineData("Basic")]
    public async Task TheSchemeIsMatchedWithoutRegardToCase(string scheme)
    {
        using var client = _app.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "v3/index.json");
        request.Headers.TryAddWithoutValidation("Authorization", $"{scheme} {Encode($"{_app.TestUserName}:{_app.TestPassword}")}");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnotherSchemeWithValidCredentials_IsNotAccepted()
    {
        using var client = _app.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "v3/index.json");
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"NugetBasicAuthentication {Encode($"{_app.TestUserName}:{_app.TestPassword}")}");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Basic !!!not-base64!!!")]
    [InlineData("Basic")]
    [InlineData("Basic    ")]
    [InlineData("Basic dGVzdC11c2Vy")] // "test-user": no colon
    [InlineData("Basic OnBhc3N3b3Jk")] // ":password": empty user name
    public async Task MalformedHeaders_AreRejectedWithA401NeverA500(string header)
    {
        using var client = _app.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "v3/index.json");
        request.Headers.TryAddWithoutValidation("Authorization", header);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(new[] { Challenge }, response.Headers.GetValues("WWW-Authenticate"));
    }

    [Fact]
    public async Task BytesThatAreNotUtf8_AreRejectedWithA401()
    {
        var bytes = new byte[] { 0x61, 0xFF, 0xFE, (byte)':', 0x62 };

        var status = await StatusFor($"Basic {Convert.ToBase64String(bytes)}");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task AHeaderOverTheSizeCap_IsRejectedWithA401()
    {
        var status = await StatusFor("Basic " + new string('A', 5000));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task APasswordContainingColons_Authenticates()
    {
        const string password = "pa:ss:word: with colons 1234";
        await AddUserAsync("colon-user", password);

        using var client = _app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = Basic("colon-user", password);
        using var response = await client.GetAsync("v3/index.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UserNamesAreCaseInsensitive()
    {
        using var client = _app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = Basic(_app.TestUserName.ToUpperInvariant(), _app.TestPassword);

        using var response = await client.GetAsync("v3/index.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("v3/index.json")]
    [InlineData("v3/search?q=anything")]
    [InlineData("v3/autocomplete?q=anything")]
    [InlineData("v3/registration/some.package/index.json")]
    [InlineData("v3/package/some.package/index.json")]
    public async Task WithNoUserAtAll_EveryProtectedEndpointReturns401(string path)
    {
        using var emptyApp = new ElPaquetoApplication(_output, seedTestUser: false);
        using var client = emptyApp.CreateClient();

        using var anonymous = await client.GetAsync(path);
        client.DefaultRequestHeaders.Authorization = Basic("admin", "admin");
        using var guessed = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, guessed.StatusCode);
    }

    [Fact]
    public async Task DisablingAUserInTheDatabase_AppliesToTheNextRequestEvenWithCachedCredentials()
    {
        using var client = _app.CreateClient();
        using (var first = await client.GetAsync("v3/index.json"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        await UpdateUserAsync(_app.TestUserName, user => user.IsActive = false);

        using var second = await client.GetAsync("v3/index.json");
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task ChangingThePasswordInTheDatabase_MakesTheOldPasswordFailAtOnce()
    {
        using var client = _app.CreateClient();
        using (var first = await client.GetAsync("v3/index.json"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        await UpdateUserAsync(_app.TestUserName, (user, passwords) => user.PasswordHash = passwords.Hash("a brand new passphrase 123"));

        using var oldPassword = await client.GetAsync("v3/index.json");
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);

        using var newClient = _app.CreateAnonymousClient();
        newClient.DefaultRequestHeaders.Authorization = Basic(_app.TestUserName, "a brand new passphrase 123");
        using var newPassword = await newClient.GetAsync("v3/index.json");
        Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);
    }

    [Fact]
    public async Task OneHundredAuthenticatedRequests_PerformExactlyOnePasswordVerification()
    {
        var counter = new CountingPasswordHasher(new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = 100_000,
        })));
        using var app = _app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPasswordHasher<User>>();
            services.AddSingleton<IPasswordHasher<User>>(counter);
        }));
        using var client = app.CreateClient();

        for (var i = 0; i < 100; i++)
        {
            using var response = await client.GetAsync("v3/index.json");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(1, counter.VerifyCalls);
    }

    [Fact]
    public async Task WithTheCacheDisabled_EveryRequestIsVerified()
    {
        using var app = new ElPaquetoApplication(
            _output,
            inMemoryConfiguration: dict => dict["Security:Authentication:VerificationCacheTtl"] = "00:00:00");
        var counter = new CountingPasswordHasher(new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = 100_000,
        })));
        using var counting = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPasswordHasher<User>>();
            services.AddSingleton<IPasswordHasher<User>>(counter);
        }));
        using var client = counting.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            using var response = await client.GetAsync("v3/index.json");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(3, counter.VerifyCalls);
    }

    [Fact]
    public async Task AHashBelowTheConfiguredCost_IsStoredAgainAfterOneSuccessfulLogin()
    {
        using var app = new ElPaquetoApplication(
            _output,
            inMemoryConfiguration: dict => dict["Security:Passwords:IterationCount"] = "150000");
        var lowCost = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = 100_000,
        }));
        const string password = "an old hash from a cheaper time";
        await AddUserAsync(app, "legacy-user", lowCost.HashPassword(new User(), password));

        Assert.Equal(100_000, IterationsOf(await HashOfAsync(app, "legacy-user")));

        using var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = Basic("legacy-user", password);
        using var response = await client.GetAsync("v3/index.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(150_000, IterationsOf(await HashOfAsync(app, "legacy-user")));
    }

    [Fact]
    public async Task AnonymousRequests_NeverTouchTheSecurityDatabaseFile()
    {
        var connectionString = _app.Services.GetRequiredService<IOptions<ElPaqueto.Security.Configuration.ElPaquetoSecurityOptions>>()
            .Value.Database.ConnectionString;
        var path = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            using var client = _app.CreateAnonymousClient();

            // A request without credentials must be answered without opening the database for writing:
            // doing so per request would drop the locks SQLite holds and fail every route when the file is busy.
            using var index = await client.GetAsync("v3/index.json");
            using var page = await client.GetAsync("/");
            using var health = await client.GetAsync("health");

            Assert.Equal(HttpStatusCode.Unauthorized, index.StatusCode);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.NotEqual(HttpStatusCode.InternalServerError, page.StatusCode);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task EveryNfkcSpellingOfThePassword_SharesOneCacheEntryAndOneVerification()
    {
        var counter = new CountingPasswordHasher(new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = 100_000,
        })));
        using var app = _app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPasswordHasher<User>>();
            services.AddSingleton<IPasswordHasher<User>>(counter);
        }));
        using var client = app.CreateClient();

        // Fullwidth forms are NFKC-equivalent to ASCII: the same password in another spelling.
        var fullwidth = new string(_app.TestPassword.Select(c => (char)(c - '!' + '！')).ToArray());
        foreach (var password in new[] { _app.TestPassword, fullwidth, _app.TestPassword, fullwidth })
        {
            client.DefaultRequestHeaders.Authorization = Basic(_app.TestUserName, password);
            using var response = await client.GetAsync("v3/index.json");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(1, counter.VerifyCalls);
    }

    [Fact]
    public async Task InvalidUserNamesAndMalformedHeaders_AreIndistinguishableFromAWrongPassword()
    {
        var wrongPassword = await Fingerprint(Basic(_app.TestUserName, _app.TestPassword + "x"));
        var invalidUserName = await Fingerprint(Basic("not a valid name!", _app.TestPassword));
        var longUserName = await Fingerprint(Basic(new string('a', 200), _app.TestPassword));

        Assert.Equal(wrongPassword, invalidUserName);
        Assert.Equal(wrongPassword, longUserName);

        using var client = _app.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "v3/index.json");
        request.Headers.TryAddWithoutValidation("Authorization", "Basic !!!");
        using var malformed = await client.SendAsync(request);
        using var anonymousClient = _app.CreateAnonymousClient();
        using var anonymous = await anonymousClient.GetAsync("v3/index.json");

        Assert.Equal(anonymous.StatusCode, malformed.StatusCode);
        Assert.Equal(
            anonymous.Headers.GetValues("WWW-Authenticate"),
            malformed.Headers.GetValues("WWW-Authenticate"));
    }

    [Fact]
    public async Task HeadRequests_AreAuthenticatedLikeGetRequests()
    {
        using var anonymous = _app.CreateAnonymousClient();
        using var authenticated = _app.CreateClient();

        using var denied = await anonymous.SendAsync(new HttpRequestMessage(HttpMethod.Head, "v3/search?q=anything"));
        using var allowed = await authenticated.SendAsync(new HttpRequestMessage(HttpMethod.Head, "v3/search?q=anything"));

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, allowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed.StatusCode);
    }

    [Fact]
    public async Task NoLogEntry_ContainsThePasswordTheEncodedCredentialsOrTheRawHeader()
    {
        var logs = new List<string>();
        using var app = _app.WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(new CapturingLoggerProvider(logs));
        }));
        var goodHeader = $"Basic {Encode($"{_app.TestUserName}:{_app.TestPassword}")}";
        var badPassword = _app.TestPassword + "-wrong";
        var badHeader = $"Basic {Encode($"{_app.TestUserName}:{badPassword}")}";

        foreach (var header in new[] { goodHeader, badHeader, "Basic %%%", $"Basic {Encode("unknown-user:its-secret-password")}" })
        {
            using var client = app.CreateClient();
            client.DefaultRequestHeaders.Authorization = null;
            using var request = new HttpRequestMessage(HttpMethod.Get, "v3/index.json");
            request.Headers.TryAddWithoutValidation("Authorization", header);
            using var response = await client.SendAsync(request);
        }

        string[] captured;
        lock (logs)
        {
            captured = logs.ToArray();
        }

        Assert.NotEmpty(captured);
        string[] secrets =
        [
            _app.TestPassword,
            badPassword,
            "its-secret-password",
            Encode($"{_app.TestUserName}:{_app.TestPassword}"),
            Encode($"{_app.TestUserName}:{badPassword}"),
            Encode("unknown-user:its-secret-password"),
        ];
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(captured, line => line.Contains(secret, StringComparison.Ordinal));
        }
    }

    private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static AuthenticationHeaderValue Basic(string userName, string password)
        => new("Basic", Encode($"{userName}:{password}"));

    private async Task<HttpStatusCode> StatusFor(string headerValue)
    {
        using var client = _app.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "v3/index.json");
        request.Headers.TryAddWithoutValidation("Authorization", headerValue);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    // Status, every header except Date, and the body: what a client can observe.
    private async Task<string> Fingerprint(AuthenticationHeaderValue credentials)
    {
        using var client = _app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = credentials;
        using var response = await client.GetAsync("v3/index.json");

        var headers = response.Headers
            .Concat(response.Content.Headers)
            .Where(h => !h.Key.Equals("Date", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => $"{h.Key}: {string.Join(",", h.Value)}");

        return $"{(int)response.StatusCode}|{string.Join("|", headers)}|{await response.Content.ReadAsStringAsync()}";
    }

    private static int IterationsOf(string hash)
    {
        var bytes = Convert.FromBase64String(hash);
        return (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5, 4));
    }

    private Task AddUserAsync(string userName, string password, bool isActive = true)
        => AddUserAsync(_app, userName, null, password, isActive);

    private static Task AddUserAsync(ElPaquetoApplication app, string userName, string passwordHash)
        => AddUserAsync(app, userName, passwordHash, null, true);

    private static async Task AddUserAsync(ElPaquetoApplication app, string userName, string passwordHash, string password, bool isActive)
    {
        using var scope = app.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var passwords = scope.ServiceProvider.GetRequiredService<IPasswordService>();
        var now = DateTime.UtcNow;

        database.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            PasswordHash = passwordHash ?? passwords.Hash(password),
            IsActive = isActive,
            Permissions = Permission.Read,
            CreatedAt = now,
            UpdatedAt = now,
            PasswordChangedAt = now,
        });
        await database.SaveChangesAsync();
    }

    private Task UpdateUserAsync(string userName, Action<User> change)
        => UpdateUserAsync(userName, (user, _) => change(user));

    private async Task UpdateUserAsync(string userName, Action<User, IPasswordService> change)
    {
        using var scope = _app.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var passwords = scope.ServiceProvider.GetRequiredService<IPasswordService>();
        var normalized = User.Normalize(userName);
        var user = await database.Users.SingleAsync(u => u.NormalizedUserName == normalized);
        change(user, passwords);
        await database.SaveChangesAsync();
    }

    private static async Task<string> HashOfAsync(ElPaquetoApplication app, string userName)
    {
        using var scope = app.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var normalized = User.Normalize(userName);
        return await database.Users.AsNoTracking().Where(u => u.NormalizedUserName == normalized).Select(u => u.PasswordHash).SingleAsync();
    }

    private sealed class CountingPasswordHasher(IPasswordHasher<User> inner) : IPasswordHasher<User>
    {
        private int _verifyCalls;

        public int VerifyCalls => _verifyCalls;

        public string HashPassword(User user, string password) => inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword)
        {
            System.Threading.Interlocked.Increment(ref _verifyCalls);
            return inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    private sealed class CapturingLoggerProvider(List<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(logs);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> logs) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (logs)
                {
                    logs.Add(formatter(state, exception) + (exception == null ? string.Empty : " " + exception));
                }
            }
        }
    }
}
