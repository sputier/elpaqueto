using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElPaqueto.Core;
using ElPaqueto.Security;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;
using Xunit;
using Xunit.Abstractions;
using AspNetAuthentication = Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions;

namespace ElPaqueto.Tests;

/// <summary>
/// API keys on the real host (issues #32, #34 and the write rule of #26): the <c>X-NuGet-ApiKey</c> header, keys
/// sent as Basic passwords, the order between credentials, writes reserved to header keys, and the inline check
/// of the inherited write actions.
/// </summary>
public class ApiKeyAuthenticationIntegrationTests : IDisposable
{
    private const string Password = "a long enough passphrase for tests";
    private const string PackageId = "TestData";
    private static readonly NuGetVersion PackageVersion = NuGetVersion.Parse("1.2.3");

    private readonly ITestOutputHelper _output;
    private readonly ElPaquetoApplication _app;

    public ApiKeyAuthenticationIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _app = new ElPaquetoApplication(output);
    }

    public void Dispose() => _app.Dispose();

    public static IEnumerable<object[]> Routes() =>
    [
        ["GET", "v3/index.json"],
        ["PUT", "api/v2/package"],
        ["PUT", "api/v2/symbol"],
        ["POST", "api/v2/package/testdata/1.2.3"],
        ["DELETE", "api/v2/package/testdata/1.2.3"],
    ];

    // ---- Every failure looks the same -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryKeyFailure_GivesTheSameResponseAsAWrongPassword(string method, string path)
    {
        await _app.AddUserAsync("owner", Password, Permission.Read | Permission.Publish);
        var unknownKey = await NewKeyAsync("owner");
        var valid = await NewKeyAsync("owner");
        var revoked = await NewKeyAsync("owner");
        var expired = await NewKeyAsync("owner");
        await _app.AddUserAsync("disabled-owner", Password, Permission.Read | Permission.Publish);
        var ofDisabledOwner = await NewKeyAsync("disabled-owner");
        await RevokeAsync(revoked.Key.KeyId);
        await ExpireAsync(expired.Key.KeyId);
        await DisableAsync("disabled-owner");
        await DeleteKeyRowAsync(unknownKey.Key.KeyId);

        var wrongSecret = valid.Token[..^1] + (valid.Token[^1] == 'a' ? 'b' : 'a');
        var reference = await Fingerprint(method, path, r => r.Headers.Authorization = TestUserExtensions.Basic("owner", Password + "x"));

        string[] tokens = [unknownKey.Token, wrongSecret, "epq_not-a-key", revoked.Token, expired.Token, ofDisabledOwner.Token];
        Assert.StartsWith("401", reference, StringComparison.Ordinal);
        foreach (var token in tokens)
        {
            Assert.Equal(reference, await Fingerprint(method, path, r => r.Headers.TryAddWithoutValidation(TestUserExtensions.ApiKeyHeader, token)));
        }

        // The same keys as Basic passwords (the malformed one is then just a wrong password).
        foreach (var token in tokens)
        {
            Assert.Equal(reference, await Fingerprint(method, path, r => r.Headers.Authorization = TestUserExtensions.Basic("apikey", token)));
        }

        await AssertPackageAbsentAsync();
    }

    [Theory]
    [InlineData(" {0}")]
    [InlineData("{0} ")]
    [InlineData("EPQ_{1}")]
    [InlineData("Epq_{1}")]
    [InlineData("{2}")]
    [InlineData("{0}x")]
    [InlineData("{0}\t")]
    [InlineData("{0},{0}")]
    [InlineData("{0}, {0}")]
    public async Task ATamperedHeaderValue_IsRefused(string shape)
    {
        await _app.AddUserAsync("owner", Password, Permission.Read | Permission.Publish);
        var key = await NewKeyAsync("owner");
        var value = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, key.Token, key.Token[4..], key.Token[..^1]);

        using var client = _app.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, "api/v2/package") { Content = PackageContent() };
        request.Headers.TryAddWithoutValidation(TestUserExtensions.ApiKeyHeader, value);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertPackageAbsentAsync();
    }

    [Fact]
    public async Task TwoApiKeyHeaders_AreRefusedEvenWhenBothAreValid()
    {
        await _app.AddUserAsync("owner", Password, Permission.Read | Permission.Publish);
        var first = await NewKeyAsync("owner");
        var second = await NewKeyAsync("owner");

        foreach (var values in new[] { new[] { first.Token, first.Token }, new[] { first.Token, second.Token } })
        {
            using var client = _app.CreateAnonymousClient();
            using var request = new HttpRequestMessage(HttpMethod.Put, "api/v2/package") { Content = PackageContent() };
            request.Headers.TryAddWithoutValidation(TestUserExtensions.ApiKeyHeader, values);
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // Two values reach the server as two header fields: the server does not pick one.
        var context = await _app.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/v3/index.json";
            c.Request.Headers.Append(TestUserExtensions.ApiKeyHeader, first.Token);
            c.Request.Headers.Append(TestUserExtensions.ApiKeyHeader, first.Token);
        });
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        await AssertPackageAbsentAsync();
    }

    // ---- Writes need a key in X-NuGet-ApiKey ------------------------------------------------------------------

    [Fact]
    public async Task APublishKeyInTheHeader_PushesSymbolsUnlistsAndRelists()
    {
        await _app.AddUserAsync("publisher", Password, Permission.Publish);
        var key = await NewKeyAsync("publisher", Permission.Publish);
        using var client = _app.CreateClientWithApiKey(key.Token);

        using (var push = await client.PutAsync("api/v2/package", PackageContent()))
        {
            Assert.Equal(HttpStatusCode.Created, push.StatusCode);
        }

        using (var again = await client.PutAsync("api/v2/package", PackageContent()))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        }

        using (var symbols = await client.PutAsync("api/v2/symbol", PackageContent(TestResources.SymbolPackage)))
        {
            Assert.Equal(HttpStatusCode.Created, symbols.StatusCode);
        }

        using (var unlist = await client.DeleteAsync("api/v2/package/testdata/1.2.3"))
        {
            Assert.Equal(HttpStatusCode.NoContent, unlist.StatusCode);
        }

        Assert.False((await FindPackageAsync()).Listed);

        using (var relist = await client.PostAsync("api/v2/package/testdata/1.2.3", new ByteArrayContent([])))
        {
            Assert.Equal(HttpStatusCode.OK, relist.StatusCode);
        }

        Assert.True((await FindPackageAsync()).Listed);
    }

    [Fact]
    public async Task APasswordWithPublish_CannotPushUnlistOrRelist()
    {
        await _app.AddUserAsync("publisher", Password, Permission.Read | Permission.Publish | Permission.Admin);
        await _app.AddPackageAsync(TestResources.GetResourceStream(TestResources.Package));
        using var client = _app.CreateClientFor("publisher", Password);

        await AssertWritesForbiddenAsync(client);
    }

    [Fact]
    public async Task APublishKeySentAsBasicPassword_CannotPushUnlistOrRelist()
    {
        await _app.AddUserAsync("publisher", Password, Permission.Read | Permission.Publish);
        var key = await NewKeyAsync("publisher", Permission.Read | Permission.Publish);
        await _app.AddPackageAsync(TestResources.GetResourceStream(TestResources.Package));
        using var client = _app.CreateClientWithApiKeyAsPassword(key.Token);

        await AssertWritesForbiddenAsync(client);
    }

    [Fact]
    public async Task AReadKeyInTheHeader_CannotPushUnlistOrRelist()
    {
        await _app.AddUserAsync("publisher", Password, Permission.Read | Permission.Publish);
        var key = await NewKeyAsync("publisher", Permission.Read);
        await _app.AddPackageAsync(TestResources.GetResourceStream(TestResources.Package));
        using var client = _app.CreateClientWithApiKey(key.Token);

        await AssertWritesForbiddenAsync(client);
    }

    [Fact]
    public async Task APublishKeyOfAnOwnerWhoLostPublish_CannotPush()
    {
        await _app.AddUserAsync("publisher", Password, Permission.Read | Permission.Publish);
        var key = await NewKeyAsync("publisher", Permission.Read | Permission.Publish);
        await SetPermissionsAsync("publisher", Permission.Read);
        using var client = _app.CreateClientWithApiKey(key.Token);

        using var response = await client.PutAsync("api/v2/package", PackageContent());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertPackageAbsentAsync();
    }

    // ---- Precedence between credentials -----------------------------------------------------------------------

    [Fact]
    public async Task TheHeaderKeyWins_OverValidBasicCredentials()
    {
        await _app.AddUserAsync("reader", Password, Permission.Read);
        var readKey = await NewKeyAsync("reader", Permission.Read);
        using var client = _app.CreateClientFor(_app.TestUserName, _app.TestPassword);
        client.DefaultRequestHeaders.TryAddWithoutValidation(TestUserExtensions.ApiKeyHeader, readKey.Token);

        // The Basic user holds every permission; the key does not: the key decides.
        using var push = await client.PutAsync("api/v2/package", PackageContent());
        using var details = await client.GetAsync("health/details");
        using var index = await client.GetAsync("v3/index.json");

        Assert.Equal(HttpStatusCode.Forbidden, push.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, details.StatusCode);
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        await AssertPackageAbsentAsync();
    }

    [Fact]
    public async Task AnInvalidHeaderKey_Fails_EvenWithValidBasicCredentials()
    {
        await _app.AddUserAsync("owner", Password, Permission.Read);
        var revoked = await NewKeyAsync("owner", Permission.Read);
        await RevokeAsync(revoked.Key.KeyId);

        foreach (var token in new[] { revoked.Token, "not a key" })
        {
            using var client = _app.CreateClientFor(_app.TestUserName, _app.TestPassword);
            client.DefaultRequestHeaders.TryAddWithoutValidation(TestUserExtensions.ApiKeyHeader, token);

            using var response = await client.GetAsync("v3/index.json");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // A header field with an empty value is present: it is an invalid key, not an absent one. (HttpClient drops
        // empty values, so the request is built on the server side, as Kestrel would deliver it.)
        var context = await _app.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/v3/index.json";
            c.Request.Headers.Authorization = TestUserExtensions.Basic(_app.TestUserName, _app.TestPassword).ToString();
            c.Request.Headers[TestUserExtensions.ApiKeyHeader] = string.Empty;
        });
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // ---- Keys as Basic passwords (restore) --------------------------------------------------------------------

    [Theory]
    [InlineData("apikey")]
    [InlineData("anything-at-all")]
    [InlineData("not a valid user name!")]
    public async Task AReadKeyAsBasicPassword_Restores_WhateverTheUserName(string userName)
    {
        await _app.AddUserAsync("ci", Password, Permission.Read);
        var key = await NewKeyAsync("ci", Permission.Read);
        await _app.AddPackageAsync(TestResources.GetResourceStream(TestResources.Package));
        using var client = _app.CreateClientWithApiKeyAsPassword(key.Token, userName);

        using var index = await client.GetAsync("v3/index.json");
        using var download = await client.GetAsync("v3/package/testdata/1.2.3/testdata.1.2.3.nupkg");

        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
    }

    [Fact]
    public async Task TheUserNameNeverSelectsAnotherUser()
    {
        await _app.AddUserAsync("ci", Password, Permission.Read);
        var key = await NewKeyAsync("ci", Permission.Read);

        // The test user holds Admin; naming it next to a key of "ci" must not lend its permissions.
        using var client = _app.CreateClientWithApiKeyAsPassword(key.Token, _app.TestUserName);
        using var details = await client.GetAsync("health/details");

        Assert.Equal(HttpStatusCode.Forbidden, details.StatusCode);
    }

    [Fact]
    public async Task AnEmptyUserNameWithAValidKey_IsRefused()
    {
        await _app.AddUserAsync("ci", Password, Permission.Read);
        var key = await NewKeyAsync("ci", Permission.Read);
        using var client = _app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + key.Token)));

        using var response = await client.GetAsync("v3/index.json");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task APublishOnlyKeyAsBasicPassword_CannotRead()
    {
        await _app.AddUserAsync("ci", Password, Permission.Read | Permission.Publish);
        var key = await NewKeyAsync("ci", Permission.Publish);
        using var client = _app.CreateClientWithApiKeyAsPassword(key.Token);

        using var response = await client.GetAsync("v3/index.json");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ATokenShapedPassword_IsNeverTriedAsAUserPassword()
    {
        // Stored directly: the password policy refuses this shape, which is what the handler relies on.
        var tokenShaped = "epq_" + new string('A', ApiKeyToken.KeyIdLength) + "_" + new string('b', ApiKeyToken.SecretLength);
        Assert.True(ApiKeyToken.IsTokenFormat(tokenShaped));
        await _app.AddUserAsync("odd-user", tokenShaped, Permission.Read);
        using var client = _app.CreateClientFor("odd-user", tokenShaped);

        using var response = await client.GetAsync("v3/index.json");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- State is read on every request ----------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARevokedExpiredOrDisabledOwnersKey_FailsOnTheNextRequest(bool inHeader)
    {
        await _app.AddUserAsync("owner", Password, Permission.Read);
        var toRevoke = await NewKeyAsync("owner", Permission.Read);
        var toExpire = await NewKeyAsync("owner", Permission.Read);
        await _app.AddUserAsync("to-disable", Password, Permission.Read);
        var ofOwnerToDisable = await NewKeyAsync("to-disable", Permission.Read);

        var cases = new (ApiKeyCreated Key, Func<Task> Change)[]
        {
            (toRevoke, () => RevokeAsync(toRevoke.Key.KeyId)),
            (toExpire, () => ExpireAsync(toExpire.Key.KeyId)),
            (ofOwnerToDisable, () => DisableAsync("to-disable")),
        };

        foreach (var (key, change) in cases)
        {
            using var client = inHeader ? _app.CreateClientWithApiKey(key.Token) : _app.CreateClientWithApiKeyAsPassword(key.Token);
            using (var before = await client.GetAsync("v3/index.json"))
            {
                Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            }

            await change();

            using var after = await client.GetAsync("v3/index.json");
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        }
    }

    [Fact]
    public async Task KeyPermissions_AreIntersectedWithTheOwnersCurrentPermissionsOnEveryRequest()
    {
        await _app.AddUserAsync("owner", Password, Permission.Read | Permission.Publish);
        var key = await NewKeyAsync("owner", Permission.Read | Permission.Publish);
        using var client = _app.CreateClientWithApiKey(key.Token);

        using (var before = await client.GetAsync("v3/index.json"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        await SetPermissionsAsync("owner", Permission.Publish);
        using (var demoted = await client.GetAsync("v3/index.json"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, demoted.StatusCode);
        }

        // Granting the owner more than the key holds does not widen the key.
        await SetPermissionsAsync("owner", Permission.Read | Permission.Publish | Permission.Admin);
        using var restored = await client.GetAsync("v3/index.json");
        using var details = await client.GetAsync("health/details");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, details.StatusCode);
    }

    [Fact]
    public async Task AUse_RecordsLastUsedAt()
    {
        await _app.AddUserAsync("owner", Password, Permission.Read);
        var key = await NewKeyAsync("owner", Permission.Read);
        using var client = _app.CreateClientWithApiKey(key.Token);

        using var response = await client.GetAsync("v3/index.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _app.Services.CreateScope();
        var keys = await scope.ServiceProvider.GetRequiredService<IApiKeyService>().ListAsync();
        Assert.NotNull(keys.Single(k => k.KeyId == key.Key.KeyId).LastUsedAt);
    }

    // ---- The inline check of the inherited write actions -------------------------------------------------------

    [Fact]
    public void TheInlineCheck_IsTheNewServiceAndNotTheInheritedOne()
    {
        using var scope = _app.Services.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<Core.IAuthenticationService>();
        var registrations = scope.ServiceProvider.GetServices<Core.IAuthenticationService>().ToList();

        Assert.IsType<ApiKeyPrincipalAuthenticationService>(service);
        Assert.Single(registrations);
        Assert.DoesNotContain(registrations, r => r is ApiKeyAuthenticationService);
    }

    [Fact]
    public async Task TheInlineCheck_AcceptsOnlyTheHeaderKeyOfTheRequestWithPublish_WithoutQueryingTheDatabase()
    {
        var logs = new List<(string Category, string Message)>();
        using var app = _app.WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(new CapturingLoggerProvider(logs));
        }));
        await app.AddUserAsync("owner", Password, Permission.Read | Permission.Publish);
        var publishKey = await app.AddApiKeyAsync("owner", Permission.Publish);
        var otherPublishKey = await app.AddApiKeyAsync("owner", Permission.Publish);
        var readKey = await app.AddApiKeyAsync("owner", Permission.Read);

        Assert.True(await InlineCheckAsync(app, logs, Header(publishKey.Token), publishKey.Token));
        Assert.False(await InlineCheckAsync(app, logs, Header(publishKey.Token), otherPublishKey.Token));
        Assert.False(await InlineCheckAsync(app, logs, Header(publishKey.Token), null));
        Assert.False(await InlineCheckAsync(app, logs, Header(publishKey.Token), "not a key"));
        Assert.False(await InlineCheckAsync(app, logs, Header(readKey.Token), readKey.Token));
        Assert.False(await InlineCheckAsync(app, logs, BasicPassword(publishKey.Token), publishKey.Token));
        Assert.False(await InlineCheckAsync(app, logs, BasicPassword("owner", Password), publishKey.Token));
        Assert.False(await InlineCheckAsync(app, logs, _ => { }, publishKey.Token));

        // No request at all: no principal.
        using var scope = app.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<Core.IAuthenticationService>()
            .AuthenticateAsync(publishKey.Token, CancellationToken.None));
    }

    [Fact]
    public async Task TheLegacyApiKeySettings_NeitherOpenPublishingNorAuthenticate()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: dict =>
        {
            dict["ApiKey"] = "legacy-single-key";
            dict["Authentication:ApiKeys:0:Key"] = "legacy-listed-key";
        });
        using var noSettings = new ElPaquetoApplication(_output);

        foreach (var (host, value) in new[] { (app, "legacy-single-key"), (app, "legacy-listed-key"), (noSettings, "anything") })
        {
            using var client = host.CreateClientWithApiKey(value);
            using var response = await client.PutAsync("api/v2/package", PackageContent());
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            // The inherited service answered true here when no key was configured: anyone could publish.
            using var scope = host.Services.CreateScope();
            Assert.False(await scope.ServiceProvider.GetRequiredService<Core.IAuthenticationService>()
                .AuthenticateAsync(value, CancellationToken.None));
        }

        using var anonymous = noSettings.CreateAnonymousClient();
        using var anonymousPush = await anonymous.PutAsync("api/v2/package", PackageContent());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousPush.StatusCode);
    }

    // ---- Logs -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoLogEntry_ContainsATokenOrASecret_AndFailuresNameTheKeyId()
    {
        var logs = new List<(string Category, string Message)>();
        using var app = _app.WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(new CapturingLoggerProvider(logs));
        }));
        await app.AddUserAsync("owner", Password, Permission.Read | Permission.Publish);
        var valid = await app.AddApiKeyAsync("owner", Permission.Read | Permission.Publish);
        var revoked = await app.AddApiKeyAsync("owner", Permission.Read);
        using (var scope = app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IApiKeyService>().RevokeAsync(revoked.Key.KeyId);
        }

        var wrongSecret = valid.Token[..^3] + "zzz";
        foreach (var token in new[] { valid.Token, revoked.Token, wrongSecret })
        {
            using (var header = app.CreateClientWithApiKey(token))
            using (await header.PutAsync("api/v2/package", PackageContent()))
            {
            }

            using var basic = app.CreateClientWithApiKeyAsPassword(token);
            using (await basic.GetAsync("v3/index.json"))
            {
            }
        }

        string[] captured;
        lock (logs)
        {
            captured = logs.Select(l => l.Message).ToArray();
        }

        Assert.NotEmpty(captured);
        foreach (var token in new[] { valid.Token, revoked.Token, wrongSecret })
        {
            Assert.DoesNotContain(captured, line => line.Contains(token, StringComparison.Ordinal));
            Assert.DoesNotContain(captured, line => line.Contains(token[(4 + ApiKeyToken.KeyIdLength + 1)..], StringComparison.Ordinal));
            Assert.DoesNotContain(captured, line => line.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("apikey:" + token)), StringComparison.Ordinal));
        }

        Assert.Contains(captured, line => line.Contains(revoked.Key.KeyId, StringComparison.Ordinal) && line.Contains("Revoked", StringComparison.Ordinal));
        Assert.Contains(captured, line => line.Contains(valid.Key.KeyId, StringComparison.Ordinal) && line.Contains("WrongSecret", StringComparison.Ordinal));
    }

    // ---- Helpers --------------------------------------------------------------------------------------------

    private static Action<HttpContext> Header(string token) => c => c.Request.Headers[TestUserExtensions.ApiKeyHeader] = token;

    private static Action<HttpContext> BasicPassword(string token) => BasicPassword("apikey", token);

    private static Action<HttpContext> BasicPassword(string userName, string password)
        => c => c.Request.Headers.Authorization = TestUserExtensions.Basic(userName, password).ToString();

    // Authenticates a request with the real handler, then asks the inline check while counting the SQL commands it runs.
    private static async Task<bool> InlineCheckAsync(
        WebApplicationFactory<Startup> app,
        List<(string Category, string Message)> logs,
        Action<HttpContext> credentials,
        string presentedKey)
    {
        using var scope = app.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        credentials(context);
        var result = await AspNetAuthentication.AuthenticateAsync(context);
        if (result.Succeeded)
        {
            context.User = result.Principal;
        }

        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        var commandsBefore = CountDatabaseCommands(logs);

        var accepted = await scope.ServiceProvider.GetRequiredService<Core.IAuthenticationService>()
            .AuthenticateAsync(presentedKey, CancellationToken.None);

        Assert.Equal(commandsBefore, CountDatabaseCommands(logs));
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        return accepted;
    }

    private static int CountDatabaseCommands(List<(string Category, string Message)> logs)
    {
        lock (logs)
        {
            return logs.Count(l => l.Category == DbLoggerCategory.Database.Command.Name && l.Message.Contains("Executed DbCommand", StringComparison.Ordinal));
        }
    }

    private async Task<string> Fingerprint(string method, string path, Action<HttpRequestMessage> credentials)
    {
        using var client = _app.CreateAnonymousClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "PUT" or "POST")
        {
            request.Content = path.EndsWith("symbol", StringComparison.Ordinal) ? PackageContent(TestResources.SymbolPackage) : PackageContent();
        }

        credentials(request);
        using var response = await client.SendAsync(request);

        var headers = response.Headers
            .Concat(response.Content.Headers)
            .Where(h => !h.Key.Equals("Date", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => $"{h.Key}: {string.Join(",", h.Value)}");

        return $"{(int)response.StatusCode}|{string.Join("|", headers)}|{await response.Content.ReadAsStringAsync()}";
    }

    private static MultipartFormDataContent PackageContent(string resource = TestResources.Package)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(TestResources.GetResourceStream(resource).ToArray()), "package", "package.nupkg");
        return content;
    }

    private async Task AssertWritesForbiddenAsync(HttpClient client)
    {
        using var push = await client.PutAsync("api/v2/package", PackageContent());
        using var symbols = await client.PutAsync("api/v2/symbol", PackageContent(TestResources.SymbolPackage));
        using var unlist = await client.DeleteAsync("api/v2/package/testdata/1.2.3");
        Assert.True((await FindPackageAsync()).Listed, "A refused delete must leave the package listed.");
        await SetListedAsync(false);
        using var relist = await client.PostAsync("api/v2/package/testdata/1.2.3", new ByteArrayContent([]));

        Assert.Equal(HttpStatusCode.Forbidden, push.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, symbols.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unlist.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, relist.StatusCode);
        Assert.False((await FindPackageAsync()).Listed, "A refused relist must leave the package unlisted.");
    }

    private async Task<Package> FindPackageAsync()
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPackageDatabase>()
            .FindOrNullAsync(PackageId, PackageVersion, includeUnlisted: true, CancellationToken.None);
    }

    private async Task AssertPackageAbsentAsync() => Assert.Null(await FindPackageAsync());

    private async Task SetListedAsync(bool listed)
    {
        using var scope = _app.Services.CreateScope();
        var packages = scope.ServiceProvider.GetRequiredService<IPackageDatabase>();
        if (listed)
        {
            await packages.RelistPackageAsync(PackageId, PackageVersion, CancellationToken.None);
        }
        else
        {
            await packages.UnlistPackageAsync(PackageId, PackageVersion, CancellationToken.None);
        }
    }

    private Task<ApiKeyCreated> NewKeyAsync(string owner, Permission permissions = Permission.Read | Permission.Publish)
        => _app.AddApiKeyAsync(owner, permissions);

    private async Task RevokeAsync(string keyId)
    {
        using var scope = _app.Services.CreateScope();
        Assert.Equal(ApiKeyRevocation.Revoked, await scope.ServiceProvider.GetRequiredService<IApiKeyService>().RevokeAsync(keyId));
    }

    // The service refuses an expiry in the past, so the row is changed directly.
    private Task ExpireAsync(string keyId)
        => WithDatabaseAsync(db => db.ApiKeys.Where(k => k.KeyId == keyId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.ExpiresAt, DateTime.UtcNow.AddMinutes(-1))));

    private Task DeleteKeyRowAsync(string keyId)
        => WithDatabaseAsync(db => db.ApiKeys.Where(k => k.KeyId == keyId).ExecuteDeleteAsync());

    private Task DisableAsync(string userName)
        => WithDatabaseAsync(db => db.Users.Where(u => u.NormalizedUserName == User.Normalize(userName))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false)));

    private Task SetPermissionsAsync(string userName, Permission permissions)
        => WithDatabaseAsync(db => db.Users.Where(u => u.NormalizedUserName == User.Normalize(userName))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Permissions, permissions)));

    private async Task WithDatabaseAsync(Func<AuthDbContext, Task<int>> change)
    {
        using var scope = _app.Services.CreateScope();
        Assert.Equal(1, await change(scope.ServiceProvider.GetRequiredService<AuthDbContext>()));
    }

    private sealed class CapturingLoggerProvider(List<(string Category, string Message)> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, logs);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, List<(string Category, string Message)> logs) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (logs)
                {
                    logs.Add((category, formatter(state, exception) + (exception == null ? string.Empty : " " + exception)));
                }
            }
        }
    }
}
