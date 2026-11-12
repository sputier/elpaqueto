using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ElPaqueto.Security;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Data;
using ElPaqueto.Security.Passwords;
using ElPaqueto.Security.Tests.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// The security audit trail through the whole server pipeline (#22): every authentication, authorization and
/// transport decision leaves its event with the client address after trusted forwarded-header processing, and no
/// log entry of any category (message, structured values, scopes, exception text) ever contains a credential.
/// </summary>
/// <remarks>
/// Requests are sent with <c>TestServer.SendAsync</c> so that each one sets its client address and scheme, as
/// Kestrel would. Each scenario uses its own documentation-range address, so that throttling only trips where the
/// test wants it to.
/// </remarks>
public class AuditLoggingIntegrationTests : IDisposable
{
    private const string AuditCategory = "ElPaqueto.Security.Audit";
    private const string UserName = "audit-user";
    private const string DisabledUserName = "audit-disabled";
    private const string ReaderName = "audit-reader";
    private const string LoopbackProxy = "127.0.0.1";

    // Unique per run: a match in the logs can only come from this test's requests.
    private readonly string _password = "Sentinel-Pw-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly RecordingLoggerProvider _recorder = new();
    private readonly List<string> _sentAuthorizationValues = [];
    private readonly ElPaquetoApplication _baseApp;
    private readonly WebApplicationFactory<Startup> _app;

    public AuditLoggingIntegrationTests(ITestOutputHelper output)
    {
        _baseApp = new ElPaquetoApplication(output);
        _app = _baseApp.WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddFilter<RecordingLoggerProvider>(null, LogLevel.Trace);
            logging.AddProvider(_recorder);
        }));
    }

    public void Dispose()
    {
        _app.Dispose();
        _baseApp.Dispose();
    }

    [Fact]
    public async Task EveryDecision_IsAudited_AndNoLogEntryContainsACredential()
    {
        var userId = await AddUserAsync(UserName, Permission.Read, active: true);
        await AddUserAsync(DisabledUserName, Permission.Read, active: false);
        await AddUserAsync(ReaderName, Permission.Read, active: true);

        var wrongSecretKey = ApiKeyToken.Format(_baseApp.TestApiKeyId, ApiKeyGenerator.Default.GenerateSecret());
        var malformedKey = "not-a-key-" + _password;
        var passwordAsUserName = _password + "!";
        const string ForgedLine = "FORGED-AUDIT-ENTRY";

        // Passwords.
        Assert.Equal(200, await StatusAsync("203.0.113.1", Basic(UserName, _password)));
        Assert.Equal(401, await StatusAsync("203.0.113.2", Basic(UserName, _password + "x")));
        Assert.Equal(401, await StatusAsync("203.0.113.3", Basic("audit-nobody", _password)));
        Assert.Equal(401, await StatusAsync("203.0.113.4", Basic(DisabledUserName, _password)));
        Assert.Equal(401, await StatusAsync("203.0.113.5", "Basic " + Base64(UserName + _password)));
        Assert.Equal(401, await StatusAsync("203.0.113.5", "Basic " + _password));
        Assert.Equal(401, await StatusAsync("203.0.113.6", Basic(passwordAsUserName, "irrelevant password")));
        Assert.Equal(401, await StatusAsync("203.0.113.7", Basic("evil\r\n" + ForgedLine, _password)));

        // API keys, in the header and as a Basic password.
        Assert.Equal(200, await StatusAsync("203.0.113.20", apiKey: _baseApp.TestApiKey));
        Assert.Equal(200, await StatusAsync("203.0.113.21", Basic(UserName, _baseApp.TestApiKey)));
        Assert.Equal(401, await StatusAsync("203.0.113.22", apiKey: wrongSecretKey));
        Assert.Equal(401, await StatusAsync("203.0.113.23", apiKey: malformedKey));

        // Throttling: five failures from one client trip it; the sixth attempt is refused unverified.
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(401, await StatusAsync("198.51.100.77", Basic(UserName, _password + "-wrong")));
        }

        Assert.Equal(429, await StatusAsync("198.51.100.77", Basic(UserName, _password)));

        // Authorization: an authenticated caller without the permission, and a write without an API key header,
        // with a body that must never be logged.
        Assert.Equal(403, await StatusAsync("203.0.113.30", Basic(ReaderName, _password), path: "/health/details"));
        Assert.Equal(403, await StatusAsync(
            "203.0.113.31", Basic(ReaderName, _password), path: "/api/v2/package", method: "PUT", body: _password));

        // Transport: a remote plain-HTTP request is refused before its credentials are looked at.
        Assert.Equal(403, await StatusAsync("203.0.113.40", Basic(UserName, _password), scheme: "http"));

        // Behind the trusted (loopback) proxy: the audited address is the forwarded client's.
        Assert.Equal(401, await StatusAsync(LoopbackProxy, Basic(UserName, _password + "y"), forwardedFor: "192.0.2.44", forwardedProto: "https"));
        Assert.Equal(403, await StatusAsync(LoopbackProxy, Basic(UserName, _password), forwardedFor: "192.0.2.45", forwardedProto: "http"));

        var records = _recorder.Records;
        var audit = records.Where(r => r.Category == AuditCategory).ToList();
        Assert.DoesNotContain(records, r => r.EventId.Id is >= 1000 and < 3000 && r.Category != AuditCategory && r.Category.StartsWith("ElPaqueto", StringComparison.Ordinal));

        // Sign-ins.
        var success = Single(audit, 1001, r => r["ClientAddress"] == "203.0.113.1");
        Assert.Equal(LogLevel.Debug, success.Level);
        Assert.Equal("password", success["Method"]);
        Assert.Equal(userId.ToString("D"), success["UserId"]);
        Assert.Equal("api_key", Single(audit, 1001, r => r["ClientAddress"] == "203.0.113.20")["Method"]);
        Assert.Equal(_baseApp.TestApiKeyId, Single(audit, 1001, r => r["ClientAddress"] == "203.0.113.21")["KeyId"]);

        AssertFailure(audit, "203.0.113.2", "bad_password", UserName, LogLevel.Warning);
        AssertFailure(audit, "203.0.113.3", "unknown_user", "audit-nobody", LogLevel.Warning);
        AssertFailure(audit, "203.0.113.4", "disabled", DisabledUserName, LogLevel.Warning);
        Assert.Equal(2, audit.Count(r => r.EventId.Id == 1002 && r["ClientAddress"] == "203.0.113.5" && r["Reason"] == "malformed" && r.Level == LogLevel.Debug));

        // A value outside the user name rules is never logged, only its fingerprint.
        var invalidName = Single(audit, 1002, r => r["ClientAddress"] == "203.0.113.6");
        Assert.Matches("^<invalid:[0-9a-f]{8}>$", invalidName["UserName"]);
        var forged = Single(audit, 1002, r => r["ClientAddress"] == "203.0.113.7");
        Assert.Matches("^<invalid:[0-9a-f]{8}>$", forged["UserName"]);
        Assert.DoesNotContain(records, r => r.AllText.Contains(ForgedLine, StringComparison.Ordinal));

        // API key refusals: the key id only.
        var wrongSecret = Single(audit, 1003, r => r["ClientAddress"] == "203.0.113.22");
        Assert.Equal(LogLevel.Warning, wrongSecret.Level);
        Assert.Equal("wrong_secret", wrongSecret["Reason"]);
        Assert.Equal(_baseApp.TestApiKeyId, wrongSecret["KeyId"]);
        Assert.Equal("x-nuget-apikey", wrongSecret["CredentialSource"]);
        var malformed = Single(audit, 1003, r => r["ClientAddress"] == "203.0.113.23");
        Assert.Equal(LogLevel.Debug, malformed.Level);
        Assert.Equal("-", malformed["KeyId"]);

        // Throttling: the trip once, and the refused attempt.
        Assert.Contains(audit, r => r.EventId.Id == 1801 && r.Level == LogLevel.Warning && r["Client"] == "198.51.100.77");
        var throttled = Single(audit, 1004, r => r["ClientAddress"] == "198.51.100.77");
        Assert.Equal("429", throttled["StatusCode"]);

        // Authorization denials.
        var denied = Single(audit, 1101, r => r["ClientAddress"] == "203.0.113.30");
        Assert.Equal(LogLevel.Warning, denied.Level);
        Assert.Equal("health-details", denied["Endpoint"]);
        Assert.Equal("missing_permission", denied["Reason"]);
        Assert.Equal("Admin", denied["RequiredPermission"]);
        Assert.Equal("password", denied["Method"]);
        var write = Single(audit, 1101, r => r["ClientAddress"] == "203.0.113.31");
        Assert.Equal("upload-package", write["Endpoint"]);
        Assert.Equal("PUT", write["HttpMethod"]);
        Assert.Equal("api_key_header_required", write["Reason"]);

        // Transport, and the forwarded client addresses.
        Assert.Equal("http", Single(audit, 1201, r => r["ClientAddress"] == "203.0.113.40")["Scheme"]);
        AssertFailure(audit, "192.0.2.44", "bad_password", UserName, LogLevel.Warning);
        Single(audit, 1201, r => r["ClientAddress"] == "192.0.2.45");
        Assert.DoesNotContain(audit, r => r["ClientAddress"] == LoopbackProxy);

        // No credential anywhere, in any category.
        var configuration = _app.Services.GetRequiredService<IConfiguration>();
        var secrets = new List<string>
        {
            _password,
            Base64(_password),
            _baseApp.TestApiKey,
            Secret(_baseApp.TestApiKey),
            wrongSecretKey,
            Secret(wrongSecretKey),
            malformedKey,
            configuration["Security:Database:ConnectionString"],
            configuration["Database:ConnectionString"],
        };
        secrets.AddRange(_sentAuthorizationValues);
        secrets.AddRange(_sentAuthorizationValues.Select(v => v.Substring(v.IndexOf(' ', StringComparison.Ordinal) + 1)).Where(v => v.Length > 0));
        LogRecords.AssertContainNone(records, [.. secrets.Distinct()]);
    }

    private static LogRecord Single(List<LogRecord> audit, int eventId, Func<LogRecord, bool> match)
    {
        var found = audit.Where(r => r.EventId.Id == eventId && match(r)).ToList();
        Assert.True(found.Count == 1, $"Expected one event {eventId}, found {found.Count}:{Environment.NewLine}{string.Join(Environment.NewLine, audit)}");
        return found[0];
    }

    private static void AssertFailure(List<LogRecord> audit, string client, string reason, string userName, LogLevel level)
    {
        var failure = Single(audit, 1002, r => r["ClientAddress"] == client);
        Assert.Equal(reason, failure["Reason"]);
        Assert.Equal(userName, failure["UserName"]);
        Assert.Equal("password", failure["Method"]);
        Assert.Equal(level, failure.Level);
    }

    private static string Secret(string token) => ApiKeyToken.TryParse(token, out var parts) ? parts.Secret : token;

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private string Basic(string userName, string password)
    {
        var value = "Basic " + Base64(userName + ":" + password);
        _sentAuthorizationValues.Add(value);
        return value;
    }

    private async Task<int> StatusAsync(
        string client,
        string authorization = null,
        string apiKey = null,
        string path = "/v3/index.json",
        string method = "GET",
        string scheme = "https",
        string body = null,
        string forwardedFor = null,
        string forwardedProto = null)
    {
        if (authorization is not null)
        {
            _sentAuthorizationValues.Add(authorization);
        }

        var context = await _app.Server.SendAsync(context =>
        {
            context.Request.Method = method;
            context.Request.Scheme = scheme;
            context.Request.Host = new HostString("localhost");
            context.Request.Path = path;
            context.Connection.RemoteIpAddress = IPAddress.Parse(client);

            if (authorization is not null)
            {
                context.Request.Headers.Authorization = authorization;
            }

            if (apiKey is not null)
            {
                context.Request.Headers["X-NuGet-ApiKey"] = apiKey;
            }

            if (forwardedFor is not null)
            {
                context.Request.Headers["X-Forwarded-For"] = forwardedFor;
                context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
            }

            if (body is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Request.Body = new MemoryStream(bytes);
                context.Request.ContentLength = bytes.Length;
                context.Request.ContentType = "application/octet-stream";
            }
        });

        return context.Response.StatusCode;
    }

    private async Task<Guid> AddUserAsync(string userName, Permission permissions, bool active)
    {
        using var scope = _app.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var passwords = scope.ServiceProvider.GetRequiredService<IPasswordService>();
        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            PasswordHash = passwords.Hash(_password),
            IsActive = active,
            Permissions = permissions,
            CreatedAt = now,
            UpdatedAt = now,
            PasswordChangedAt = now,
        };
        database.Users.Add(user);
        await database.SaveChangesAsync();
        return user.Id;
    }
}
