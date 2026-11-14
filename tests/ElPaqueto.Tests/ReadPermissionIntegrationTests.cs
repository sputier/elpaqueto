using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ElPaqueto.Security;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// The read surface (#25) route by route, with GET and HEAD, for every kind of failed credential and every
/// combination of permissions without <c>Read</c>: no route, no method and no package id answers differently.
/// </summary>
public class ReadPermissionIntegrationTests : IDisposable
{
    private const string Password = "a long enough passphrase for tests";
    private const string Challenge = "Basic realm=\"ElPaqueto\"";

    private readonly ITestOutputHelper _output;
    private readonly ElPaquetoApplication _app;

    public ReadPermissionIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _app = new ElPaquetoApplication(output);
    }

    public void Dispose() => _app.Dispose();

    /// <summary>
    /// One row per read route of the permission table: a URL of the seeded package <c>TestData</c> 1.2.3 (with its
    /// symbols), the same URL for a package that does not exist, and what a <c>Read</c> user gets for the seeded
    /// package with GET and with HEAD.
    /// </summary>
    /// <remarks>
    /// HEAD is <see cref="HttpStatusCode.Forbidden"/> for the service index and the registrations: their actions
    /// accept GET only, so the router answers HEAD with its 405 endpoint, which has no row and is denied (ADR 0002,
    /// decision 5). The README and the icon are 404: the test package has neither.
    /// </remarks>
    private static readonly ReadRoute[] Routes =
    [
        new("index", "v3/index.json", "v3/index.json", HttpStatusCode.OK, HttpStatusCode.Forbidden),
        new("search", "v3/search?q=TestData", "v3/search?q=missing.package", HttpStatusCode.OK, HttpStatusCode.OK),
        new("autocomplete", "v3/autocomplete?q=TestData", "v3/autocomplete?q=missing.package", HttpStatusCode.OK, HttpStatusCode.OK),
        new("dependents", "v3/dependents?packageId=TestData", "v3/dependents?packageId=missing.package", HttpStatusCode.OK, HttpStatusCode.OK),
        new("registration-index", "v3/registration/testdata/index.json", "v3/registration/missing.package/index.json", HttpStatusCode.OK, HttpStatusCode.Forbidden),
        new("registration-leaf", "v3/registration/testdata/1.2.3.json", "v3/registration/missing.package/1.2.3.json", HttpStatusCode.OK, HttpStatusCode.Forbidden),
        new("package-versions", "v3/package/testdata/index.json", "v3/package/missing.package/index.json", HttpStatusCode.OK, HttpStatusCode.OK),
        new("package-download", "v3/package/testdata/1.2.3/testdata.1.2.3.nupkg", "v3/package/missing.package/1.2.3/missing.package.1.2.3.nupkg", HttpStatusCode.OK, HttpStatusCode.OK),
        new("package-download-manifest", "v3/package/testdata/1.2.3/testdata.nuspec", "v3/package/missing.package/1.2.3/missing.package.nuspec", HttpStatusCode.OK, HttpStatusCode.OK),
        new("package-download-readme", "v3/package/testdata/1.2.3/readme", "v3/package/missing.package/1.2.3/readme", HttpStatusCode.NotFound, HttpStatusCode.NotFound),
        new("package-download-icon", "v3/package/testdata/1.2.3/icon", "v3/package/missing.package/1.2.3/icon", HttpStatusCode.NotFound, HttpStatusCode.NotFound),
        new("symbol-download", "api/download/symbols/testdata.pdb/16F71ED8DD574AA2AD4A22D29E9C981Bffffffff/testdata.pdb", "api/download/symbols/missing.pdb/16F71ED8DD574AA2AD4A22D29E9C981Bffffffff/missing.pdb", HttpStatusCode.OK, HttpStatusCode.OK),
        new("prefixed-symbol-download", "api/download/symbols/testprefix/testdata.pdb/16F71ED8DD574AA2AD4A22D29E9C981Bffffffff/testdata.pdb", "api/download/symbols/testprefix/missing.pdb/16F71ED8DD574AA2AD4A22D29E9C981Bffffffff/missing.pdb", HttpStatusCode.OK, HttpStatusCode.OK),
        new("page:/Index", "/?q=TestData", "/?q=missing.package", HttpStatusCode.OK, HttpStatusCode.OK),
        new("page:/Package", "packages/TestData/1.2.3", "packages/missing.package/1.2.3", HttpStatusCode.OK, HttpStatusCode.OK),
        new("page:/Upload", "upload", "upload", HttpStatusCode.OK, HttpStatusCode.OK),
        new("page:/Statistics", "stats", "stats", HttpStatusCode.OK, HttpStatusCode.OK),
    ];

    private static readonly string[] ReadMethods = ["GET", "HEAD"];

    /// <summary>Every way to fail authentication on a read route.</summary>
    public static IEnumerable<object[]> FailedCredentials() =>
    [
        ["no credentials"],
        ["wrong password"],
        ["unknown user"],
        ["disabled user"],
        ["malformed Authorization header"],
        ["unknown API key in X-NuGet-ApiKey"],
        ["revoked API key in X-NuGet-ApiKey"],
        ["revoked API key as Basic password"],
        ["API key of a disabled owner"],
    ];

    /// <summary>Every way to be authenticated without holding <c>Read</c>.</summary>
    public static IEnumerable<object[]> CredentialsWithoutRead() =>
    [
        ["password, no permission"],
        ["password, Publish"],
        ["password, Admin"],
        ["password, Publish and Admin"],
        ["Publish key in X-NuGet-ApiKey"],
        ["Publish and Admin key as Basic password"],
        ["Read key of an owner who lost Read"],
    ];

    [Fact]
    public void TheRoutesOfThisTest_AreExactlyTheReadRowsOfThePermissionTable()
    {
        var readRows = EndpointPermissionTable.Rules
            .Where(rule => !rule.Anonymous && rule.Required == Permission.Read)
            .Select(rule => rule.Key)
            .Order(StringComparer.Ordinal);

        Assert.Equal(readRows, Routes.Select(route => route.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AReadUser_GetsTheUsualAnswerOnEveryReadRoute_ForGetAndHead()
    {
        await SeedAsync();
        await _app.AddUserAsync("reader", Password, Permission.Read);
        using var client = _app.CreateClientFor("reader", Password);
        var failures = new List<string>();

        foreach (var route in Routes)
        {
            foreach (var method in ReadMethods)
            {
                var expected = method == "GET" ? route.ReadGet : route.ReadHead;
                using var response = await ResponseSnapshot.SendAsync(client, method, route.Existing);
                if (response.StatusCode != expected)
                {
                    failures.Add($"{method} {route.Existing}: {(int)response.StatusCode}, expected {(int)expected}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task AReadUser_GetsA404ForAMissingPackage_SoTheIdenticalRefusalsBelowHideARealDifference()
    {
        await SeedAsync();
        await _app.AddUserAsync("reader", Password, Permission.Read);
        using var client = _app.CreateClientFor("reader", Password);

        foreach (var key in new[] { "registration-index", "registration-leaf", "package-versions", "package-download", "package-download-manifest", "symbol-download" })
        {
            var route = Routes.Single(r => r.Key == key);
            using var existing = await client.GetAsync(route.Existing);
            using var missing = await client.GetAsync(route.Missing);

            Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
    }

    [Theory]
    [MemberData(nameof(FailedCredentials))]
    public async Task EveryFailedCredential_Gets401WithTheChallenge_OnEveryReadRoute_IdenticalToNoCredentials_ForExistingAndMissingPackages(string credentialCase)
    {
        await SeedAsync();
        var credentials = await FailedCredentialAsync(credentialCase);
        using var client = _app.CreateClientWithoutRedirects();
        var failures = new List<string>();

        foreach (var route in Routes)
        {
            foreach (var method in ReadMethods)
            {
                using var reference = await ResponseSnapshot.SendAsync(client, method, route.Existing);
                using var existing = await ResponseSnapshot.SendAsync(client, method, route.Existing, credentials);
                using var missing = await ResponseSnapshot.SendAsync(client, method, route.Missing, credentials);

                var referenceFingerprint = await ResponseSnapshot.FingerprintAsync(reference);
                var existingFingerprint = await ResponseSnapshot.FingerprintAsync(existing);
                var missingFingerprint = await ResponseSnapshot.FingerprintAsync(missing);

                if (existing.StatusCode != HttpStatusCode.Unauthorized
                    || !existing.Headers.TryGetValues("WWW-Authenticate", out var challenge)
                    || !challenge.SequenceEqual([Challenge]))
                {
                    failures.Add($"{method} {route.Existing}: {existingFingerprint}");
                }

                if (existingFingerprint != referenceFingerprint)
                {
                    failures.Add($"{method} {route.Existing}: differs from no credentials: {existingFingerprint} / {referenceFingerprint}");
                }

                if (missingFingerprint != existingFingerprint)
                {
                    failures.Add($"{method} {route.Missing}: differs from the existing package: {missingFingerprint} / {existingFingerprint}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [MemberData(nameof(CredentialsWithoutRead))]
    public async Task EveryCredentialWithoutRead_Gets403WithoutChallenge_OnEveryReadRoute_IdenticallyForExistingAndMissingPackages(string credentialCase)
    {
        await SeedAsync();
        var credentials = await CredentialWithoutReadAsync(credentialCase);
        using var client = _app.CreateClientWithoutRedirects();
        var failures = new List<string>();

        foreach (var route in Routes)
        {
            foreach (var method in ReadMethods)
            {
                using var existing = await ResponseSnapshot.SendAsync(client, method, route.Existing, credentials);
                using var missing = await ResponseSnapshot.SendAsync(client, method, route.Missing, credentials);

                var existingFingerprint = await ResponseSnapshot.FingerprintAsync(existing);
                var missingFingerprint = await ResponseSnapshot.FingerprintAsync(missing);

                if (existing.StatusCode != HttpStatusCode.Forbidden || existing.Headers.Contains("WWW-Authenticate"))
                {
                    failures.Add($"{method} {route.Existing}: {existingFingerprint}");
                }

                if ((await existing.Content.ReadAsByteArrayAsync()).Length != 0)
                {
                    failures.Add($"{method} {route.Existing}: the refusal has a body");
                }

                if (missingFingerprint != existingFingerprint)
                {
                    failures.Add($"{method} {route.Missing}: differs from the existing package: {missingFingerprint} / {existingFingerprint}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task WithMirrorEnabled_NoRefusedReadOnAnyRoute_ReachesTheUpstreamSource()
    {
        using var upstream = new ElPaquetoApplication(_output);
        await upstream.AddPackageAsync(TestResources.GetResourceStream(TestResources.Package));
        var counter = new CountingHandler { InnerHandler = upstream.Server.CreateHandler() };
        using var upstreamClient = new HttpClient(counter) { BaseAddress = upstream.Server.BaseAddress };
        upstreamClient.DefaultRequestHeaders.Authorization = TestUserExtensions.Basic(upstream.TestUserName, upstream.TestPassword);
        using var downstream = new ElPaquetoApplication(_output, upstreamClient);
        await downstream.AddUserAsync("publisher", Password, Permission.Publish);
        await downstream.AddUserAsync("disabled", Password, Permission.Read, isActive: false);

        var refused = new (string Name, Action<HttpRequestMessage> Credentials, HttpStatusCode Expected)[]
        {
            ("no credentials", null, HttpStatusCode.Unauthorized),
            ("wrong password", r => r.Headers.Authorization = TestUserExtensions.Basic(downstream.TestUserName, downstream.TestPassword + "x"), HttpStatusCode.Unauthorized),
            ("disabled user", r => r.Headers.Authorization = TestUserExtensions.Basic("disabled", Password), HttpStatusCode.Unauthorized),
            ("Publish only", r => r.Headers.Authorization = TestUserExtensions.Basic("publisher", Password), HttpStatusCode.Forbidden),
        };

        using var client = downstream.CreateClientWithoutRedirects();
        foreach (var (name, credentials, expected) in refused)
        {
            foreach (var route in Routes)
            {
                foreach (var method in ReadMethods)
                {
                    using var response = await ResponseSnapshot.SendAsync(client, method, route.Existing, credentials);

                    Assert.True(counter.Count == 0, $"{name}: {method} {route.Existing} reached the upstream source.");
                    Assert.True(response.StatusCode == expected, $"{name}: {method} {route.Existing} answered {(int)response.StatusCode}.");
                }
            }
        }

        // Control: the same server, authorized, does reach the upstream source from an API route.
        using var reader = downstream.CreateClient();
        using var allowed = await reader.GetAsync("v3/package/testdata/index.json");

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.True(counter.Count > 0, "The authorized request should have queried the upstream source.");
    }

    [Fact]
    public async Task EveryUrlAdvertisedByTheServiceIndexAndTheRegistrations_IsARouteThatRequiresAuthentication()
    {
        await SeedAsync();
        using var reader = _app.CreateClient();
        var baseUrl = _app.Server.BaseAddress.ToString();
        var advertised = new List<(string Url, string Method, Permission Expected)>();

        // The resources of the service index: the publish endpoints are reached with PUT, the others with GET. The
        // two base addresses are prefixes: the protocol appends the package id, as a client does.
        using (var index = JsonDocument.Parse(await reader.GetStringAsync("v3/index.json")))
        {
            foreach (var resource in index.RootElement.GetProperty("resources").EnumerateArray())
            {
                var type = resource.GetProperty("@type").GetString();
                var id = resource.GetProperty("@id").GetString();
                var isPublish = type.StartsWith("PackagePublish/", StringComparison.Ordinal) || type.StartsWith("SymbolPackagePublish/", StringComparison.Ordinal);
                if (type.StartsWith("RegistrationsBaseUrl", StringComparison.Ordinal) || type.StartsWith("PackageBaseAddress/", StringComparison.Ordinal))
                {
                    id += "testdata/index.json";
                }

                advertised.Add((id, isPublish ? "PUT" : "GET", isPublish ? Permission.Publish : Permission.Read));
            }
        }

        // Every absolute URL of the registration index and of the registration leaf of the seeded package.
        foreach (var registration in new[] { "v3/registration/testdata/index.json", "v3/registration/testdata/1.2.3.json" })
        {
            using var document = JsonDocument.Parse(await reader.GetStringAsync(registration));
            foreach (var url in AbsoluteUrls(document.RootElement))
            {
                advertised.Add((url, "GET", Permission.Read));
            }
        }

        Assert.True(advertised.Distinct().Count() >= 8, $"Only {advertised.Distinct().Count()} distinct URLs were found.");
        var failures = new List<string>();
        foreach (var (url, method, expected) in advertised.Distinct())
        {
            if (!url.StartsWith(baseUrl, StringComparison.Ordinal))
            {
                failures.Add($"{url}: not a URL of this server");
                continue;
            }

            _output.WriteLine($"{method} {url}");
            // The request really runs, as the test user (Basic credentials, no key): reads are answered, the publish
            // URLs are refused by authorization (writes need a key in X-NuGet-ApiKey), so nothing is changed. The
            // endpoint chosen by routing stays on the returned context; if it did not, every URL would fail below.
            var uri = new Uri(url);
            var context = await _app.Server.SendAsync(c =>
            {
                c.Request.Method = method;
                c.Request.Path = uri.AbsolutePath;
                c.Request.QueryString = new QueryString(uri.Query);
                c.Request.Headers.Authorization = TestUserExtensions.Basic(_app.TestUserName, _app.TestPassword).ToString();
            });

            var key = EndpointKey.Resolve(context.GetEndpoint());
            if (key is null || !EndpointPermissionTable.TryGetRule(key, out var rule))
            {
                failures.Add($"{method} {url}: matches no row of the permission table (key {key ?? "none"})");
            }
            else if (rule.Anonymous || rule.Required != expected || !rule.Methods.Contains(method))
            {
                failures.Add($"{method} {url}: row {rule.Key} does not require exactly {expected} for {method}");
            }
        }

        _output.WriteLine($"{advertised.Distinct().Count()} advertised URLs checked.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static IEnumerable<string> AbsoluteUrls(JsonElement element)
    {
        switch (element.ValueKind)
        {
            // JSON-LD identifiers ("@context" and "@type", such as http://schema.nuget.org/catalog#Permalink) name
            // concepts, not resources of this server.
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject().Where(p => p.Name is not "@context" and not "@type"))
                {
                    foreach (var url in AbsoluteUrls(property.Value))
                    {
                        yield return url;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var url in AbsoluteUrls(item))
                    {
                        yield return url;
                    }
                }

                break;

            case JsonValueKind.String:
                var value = element.GetString();
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    yield return value;
                }

                break;
        }
    }

    private async Task SeedAsync()
    {
        await _app.AddPackageAsync(TestResources.GetResourceStream(TestResources.Package));
        await _app.AddSymbolPackageAsync(TestResources.GetResourceStream(TestResources.SymbolPackage));
    }

    private async Task<Action<HttpRequestMessage>> FailedCredentialAsync(string credentialCase)
    {
        switch (credentialCase)
        {
            case "no credentials":
                return null;

            case "wrong password":
                await _app.AddUserAsync("reader", Password, Permission.Read);
                return r => r.Headers.Authorization = TestUserExtensions.Basic("reader", Password + "x");

            case "unknown user":
                return r => r.Headers.Authorization = TestUserExtensions.Basic("nobody-here", Password);

            case "disabled user":
                await _app.AddUserAsync("disabled", Password, Permission.Read, isActive: false);
                return r => r.Headers.Authorization = TestUserExtensions.Basic("disabled", Password);

            case "malformed Authorization header":
                return r => r.Headers.TryAddWithoutValidation("Authorization", "Basic !!!not-base64!!!");

            case "unknown API key in X-NuGet-ApiKey":
                var (keyId, secret) = ApiKeyGenerator.Default.GenerateToken();
                var unknown = ApiKeyToken.Format(keyId, secret);
                return r => r.Headers.TryAddWithoutValidation(TestUserExtensions.ApiKeyHeader, unknown);

            case "revoked API key in X-NuGet-ApiKey":
                var revokedInHeader = await RevokedReadKeyAsync();
                return r => r.Headers.TryAddWithoutValidation(TestUserExtensions.ApiKeyHeader, revokedInHeader);

            case "revoked API key as Basic password":
                var revokedAsPassword = await RevokedReadKeyAsync();
                return r => r.Headers.Authorization = TestUserExtensions.Basic("apikey", revokedAsPassword);

            case "API key of a disabled owner":
                await _app.AddUserAsync("disabled-owner", Password, Permission.Read);
                var ownerKey = await _app.AddApiKeyAsync("disabled-owner", Permission.Read);
                await SetUserAsync("disabled-owner", user => user.IsActive = false);
                return r => r.Headers.TryAddWithoutValidation(TestUserExtensions.ApiKeyHeader, ownerKey.Token);

            default:
                throw new ArgumentOutOfRangeException(nameof(credentialCase), credentialCase, null);
        }
    }

    private async Task<Action<HttpRequestMessage>> CredentialWithoutReadAsync(string credentialCase)
    {
        switch (credentialCase)
        {
            case "password, no permission":
                return await PasswordUserAsync(Permission.None);

            case "password, Publish":
                return await PasswordUserAsync(Permission.Publish);

            case "password, Admin":
                return await PasswordUserAsync(Permission.Admin);

            case "password, Publish and Admin":
                return await PasswordUserAsync(Permission.Publish | Permission.Admin);

            case "Publish key in X-NuGet-ApiKey":
                await _app.AddUserAsync("owner", Password, Permission.Read | Permission.Publish);
                var publishKey = await _app.AddApiKeyAsync("owner", Permission.Publish);
                return r => r.Headers.TryAddWithoutValidation(TestUserExtensions.ApiKeyHeader, publishKey.Token);

            case "Publish and Admin key as Basic password":
                await _app.AddUserAsync("owner", Password, Permission.Read | Permission.Publish | Permission.Admin);
                var adminKey = await _app.AddApiKeyAsync("owner", Permission.Publish | Permission.Admin);
                return r => r.Headers.Authorization = TestUserExtensions.Basic("apikey", adminKey.Token);

            case "Read key of an owner who lost Read":
                await _app.AddUserAsync("owner", Password, Permission.Read | Permission.Publish);
                var readKey = await _app.AddApiKeyAsync("owner", Permission.Read);
                await SetUserAsync("owner", user => user.Permissions = Permission.Publish);
                return r => r.Headers.TryAddWithoutValidation(TestUserExtensions.ApiKeyHeader, readKey.Token);

            default:
                throw new ArgumentOutOfRangeException(nameof(credentialCase), credentialCase, null);
        }

        async Task<Action<HttpRequestMessage>> PasswordUserAsync(Permission permissions)
        {
            await _app.AddUserAsync("user", Password, permissions);
            return r => r.Headers.Authorization = TestUserExtensions.Basic("user", Password);
        }
    }

    private async Task<string> RevokedReadKeyAsync()
    {
        await _app.AddUserAsync("key-owner", Password, Permission.Read);
        var key = await _app.AddApiKeyAsync("key-owner", Permission.Read);
        using var scope = _app.Services.CreateScope();
        Assert.Equal(ApiKeyRevocation.Revoked, await scope.ServiceProvider.GetRequiredService<IApiKeyService>().RevokeAsync(key.Key.KeyId));
        return key.Token;
    }

    private async Task SetUserAsync(string userName, Action<ElPaqueto.Security.Data.User> change)
    {
        using var scope = _app.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ElPaqueto.Security.Data.AuthDbContext>();
        var normalized = ElPaqueto.Security.Data.User.Normalize(userName);
        change(database.Users.Single(u => u.NormalizedUserName == normalized));
        await database.SaveChangesAsync();
    }

    private sealed record ReadRoute(string Key, string Existing, string Missing, HttpStatusCode ReadGet, HttpStatusCode ReadHead);
}
