using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ElPaqueto.Core;
using ElPaqueto.Security;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Data;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// A credential the security suites send: a user password, an API key in <c>X-NuGet-ApiKey</c>, an API key as a
/// Basic password, or nothing.
/// </summary>
/// <param name="Name">The name that appears in the test case, such as <c>key-publish</c>.</param>
/// <param name="Granted">The permissions the server must grant when the credential is valid.</param>
/// <param name="Valid">Whether the credential authenticates at all.</param>
/// <param name="KeyInHeader">Whether the credential is an API key sent in <c>X-NuGet-ApiKey</c>.</param>
public sealed record SuiteCredential(string Name, Permission Granted, bool Valid, bool KeyInHeader);

/// <summary>
/// One host for a whole security suite, with a user or API key for every kind of credential, the test package
/// published, and the <see cref="RequestProbe"/> installed.
/// </summary>
/// <remarks>
/// Throttling limits are raised: the suites send hundreds of failed credentials on purpose, and throttling has its
/// own tests. Every other security setting is the default.
/// </remarks>
public class SecuritySuiteHost : IAsyncLifetime
{
    private const string Password = "a long enough passphrase for the security suites";
    private const Permission All = Permission.Read | Permission.Publish | Permission.Admin;

    private readonly Dictionary<string, Func<HttpClient>> _clients = new(StringComparer.Ordinal);
    private ElPaquetoApplication _application;

    /// <summary>Every credential kind, in the order of the test cases.</summary>
    public static readonly SuiteCredential[] Credentials =
    [
        new("anonymous", Permission.None, Valid: false, KeyInHeader: false),
        new("wrong-password", Permission.None, Valid: false, KeyInHeader: false),
        new("unknown-user", Permission.None, Valid: false, KeyInHeader: false),
        new("disabled-user", Permission.None, Valid: false, KeyInHeader: false),
        new("password-none", Permission.None, Valid: true, KeyInHeader: false),
        new("password-read", Permission.Read, Valid: true, KeyInHeader: false),
        new("password-publish", Permission.Publish, Valid: true, KeyInHeader: false),
        new("password-admin", Permission.Admin, Valid: true, KeyInHeader: false),
        new("password-all", All, Valid: true, KeyInHeader: false),
        new("key-read", Permission.Read, Valid: true, KeyInHeader: true),
        new("key-publish", Permission.Publish, Valid: true, KeyInHeader: true),
        new("key-admin", Permission.Admin, Valid: true, KeyInHeader: true),
        new("key-all", All, Valid: true, KeyInHeader: true),
        new("key-revoked", Permission.None, Valid: false, KeyInHeader: true),
        new("key-expired", Permission.None, Valid: false, KeyInHeader: true),
        new("key-of-disabled-user", Permission.None, Valid: false, KeyInHeader: true),
        new("key-wrong-secret", Permission.None, Valid: false, KeyInHeader: true),
        new("key-all-as-basic-password", All, Valid: true, KeyInHeader: false),
        new("key-revoked-as-basic-password", Permission.None, Valid: false, KeyInHeader: false),
    ];

    /// <summary>The host, with the probe installed.</summary>
    public WebApplicationFactory<Startup> App { get; private set; }

    /// <summary>What the host routed and ran.</summary>
    public RequestProbe Probe { get; } = new();

    /// <summary>The credential of that name.</summary>
    public static SuiteCredential Credential(string name) => Credentials.Single(c => c.Name == name);

    /// <summary>A client that sends exactly the credential of that name.</summary>
    public HttpClient ClientFor(string credential) => _clients[credential]();

    /// <summary>The configuration of the host, on top of the test defaults.</summary>
    protected virtual void Configure(Dictionary<string, string> configuration)
    {
        configuration["Security:Throttling:ClientFailureLimit"] = "1000";
        configuration["Security:Throttling:PairFailureLimit"] = "1000";
        configuration["Security:Throttling:AccountFailureLimit"] = "1000";
        configuration["Security:Throttling:NetworkFailureLimit"] = "10000";
    }

    /// <summary>Lets a derived host change the web host before it is built, for example to use Kestrel.</summary>
    protected virtual WebApplicationFactory<Startup> Customize(WebApplicationFactory<Startup> app) => app;

    public async Task InitializeAsync()
    {
        _application = new ElPaquetoApplication(new NullTestOutput(), inMemoryConfiguration: Configure);
        App = Customize(_application.WithWebHostBuilder(Probe.Install));

        await App.AddPackageAsync(TestResources.GetResourceStream(TestResources.Package));
        await App.AddSymbolPackageAsync(TestResources.GetResourceStream(TestResources.SymbolPackage));

        await App.AddUserAsync("u-none", Password, Permission.None);
        await App.AddUserAsync("u-read", Password, Permission.Read);
        await App.AddUserAsync("u-publish", Password, Permission.Publish);
        await App.AddUserAsync("u-admin", Password, Permission.Admin);
        await App.AddUserAsync("u-all", Password, All);
        await App.AddUserAsync("u-disabled", Password, All, isActive: false);
        await App.AddUserAsync("u-keys", Password, All);
        await App.AddUserAsync("u-keys-disabled", Password, All);

        var read = await App.AddApiKeyAsync("u-keys", Permission.Read);
        var publish = await App.AddApiKeyAsync("u-keys", Permission.Publish);
        var admin = await App.AddApiKeyAsync("u-keys", Permission.Admin);
        var all = await App.AddApiKeyAsync("u-keys", All);
        var revoked = await App.AddApiKeyAsync("u-keys", All);
        var expired = await App.AddApiKeyAsync("u-keys", All);
        var ofDisabledUser = await App.AddApiKeyAsync("u-keys-disabled", All);

        using (var scope = App.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IApiKeyService>().RevokeAsync(revoked.Key.KeyId);
            var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

            // The service refuses an expiry in the past, so the row is changed directly.
            await database.ApiKeys.Where(k => k.KeyId == expired.Key.KeyId)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            var disabledOwner = User.Normalize("u-keys-disabled");
            await database.Users.Where(u => u.NormalizedUserName == disabledOwner)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        _clients["anonymous"] = App.CreateAnonymousClient;
        _clients["wrong-password"] = () => App.CreateClientFor("u-all", Password + "x");
        _clients["unknown-user"] = () => App.CreateClientFor("nobody-by-that-name", Password);
        _clients["disabled-user"] = () => App.CreateClientFor("u-disabled", Password);
        _clients["password-none"] = () => App.CreateClientFor("u-none", Password);
        _clients["password-read"] = () => App.CreateClientFor("u-read", Password);
        _clients["password-publish"] = () => App.CreateClientFor("u-publish", Password);
        _clients["password-admin"] = () => App.CreateClientFor("u-admin", Password);
        _clients["password-all"] = () => App.CreateClientFor("u-all", Password);
        _clients["key-read"] = () => App.CreateClientWithApiKey(read.Token);
        _clients["key-publish"] = () => App.CreateClientWithApiKey(publish.Token);
        _clients["key-admin"] = () => App.CreateClientWithApiKey(admin.Token);
        _clients["key-all"] = () => App.CreateClientWithApiKey(all.Token);
        _clients["key-revoked"] = () => App.CreateClientWithApiKey(revoked.Token);
        _clients["key-expired"] = () => App.CreateClientWithApiKey(expired.Token);
        _clients["key-of-disabled-user"] = () => App.CreateClientWithApiKey(ofDisabledUser.Token);
        _clients["key-wrong-secret"] = () => App.CreateClientWithApiKey(WrongSecret(all.Token));
        _clients["key-all-as-basic-password"] = () => App.CreateClientWithApiKeyAsPassword(all.Token);
        _clients["key-revoked-as-basic-password"] = () => App.CreateClientWithApiKeyAsPassword(revoked.Token);

        var missing = Credentials.Select(c => c.Name).Where(name => !_clients.ContainsKey(name)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException("Credentials without a client: " + string.Join(", ", missing));
        }
    }

    public async Task DisposeAsync()
    {
        if (App is not null)
        {
            await App.DisposeAsync();
        }

        if (_application is not null)
        {
            await _application.DisposeAsync();
        }
    }

    /// <summary>
    /// What a refused request must not change: the packages, their listing, and the files in package storage.
    /// Download counters are left out, since an authorized read changes them.
    /// </summary>
    public async Task<string> SnapshotStateAsync()
    {
        using var scope = App.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IContext>();
        var packages = await context.Packages
            .OrderBy(p => p.Id).ThenBy(p => p.NormalizedVersionString)
            .Select(p => p.Id + "/" + p.NormalizedVersionString + "/listed=" + p.Listed)
            .ToListAsync();

        var storage = App.Services.GetRequiredService<IConfiguration>()["Storage:Path"];
        IEnumerable<string> files = Directory.Exists(storage)
            ? Directory.EnumerateFiles(storage, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(storage, path) + ":" + new FileInfo(path).Length)
                .OrderBy(entry => entry, StringComparer.Ordinal)
            : [];

        return string.Join("\n", packages.Concat(files));
    }

    /// <summary>The outcome the permission table prescribes for a credential on a row.</summary>
    public static HttpStatusCode? ExpectedRefusal(string key, SuiteCredential credential, bool hardDelete = false)
    {
        var rule = EndpointSamples.Rule(key);
        if (rule.Anonymous)
        {
            return null;
        }

        if (!credential.Valid)
        {
            return HttpStatusCode.Unauthorized;
        }

        if (rule.RequiresApiKeyHeader && !credential.KeyInHeader)
        {
            return HttpStatusCode.Forbidden;
        }

        var required = rule.Required;
        if (hardDelete && key == ElPaqueto.Security.Authorization.EndpointPermissionTable.DeleteKey)
        {
            required |= Permission.Admin;
        }

        return credential.Granted.HasFlag(required) ? null : HttpStatusCode.Forbidden;
    }

    // The same key id with another secret: the last character changed.
    private static string WrongSecret(string token) => token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

}

/// <summary>The same host, deleting packages for good: deleting then needs Admin on top of Publish.</summary>
public sealed class HardDeleteSecuritySuiteHost : SecuritySuiteHost
{
    protected override void Configure(Dictionary<string, string> configuration)
    {
        base.Configure(configuration);
        configuration["PackageDeletionBehavior"] = "HardDelete";
    }
}

/// <summary>
/// The same host behind a real Kestrel listener on the loopback interface, for requests sent byte for byte with
/// <see cref="RawHttp"/>: the test server would receive them already parsed and normalized by the client.
/// </summary>
public class KestrelSecuritySuiteHost : SecuritySuiteHost
{
    /// <summary>The address Kestrel listens on.</summary>
    public Uri Address { get; private set; }

    /// <summary>The headers that carry the credential of that name, and nothing else.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> HeadersFor(string credential)
    {
        using var client = ClientFor(credential);
        var headers = new List<KeyValuePair<string, string>>();
        if (client.DefaultRequestHeaders.Authorization is { } authorization)
        {
            headers.Add(new("Authorization", authorization.ToString()));
        }

        if (client.DefaultRequestHeaders.TryGetValues(TestUserExtensions.ApiKeyHeader, out var keys))
        {
            headers.Add(new(TestUserExtensions.ApiKeyHeader, keys.Single()));
        }

        return headers;
    }

    protected override WebApplicationFactory<Startup> Customize(WebApplicationFactory<Startup> app)
    {
        app.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        app.StartServer();
        Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.First());
        return app;
    }
}

/// <summary>The Kestrel host, mounted below <c>/feed</c> with the <c>PathBase</c> setting.</summary>
public sealed class PathBaseSecuritySuiteHost : KestrelSecuritySuiteHost
{
    public const string PathBase = "/feed";

    protected override void Configure(Dictionary<string, string> configuration)
    {
        base.Configure(configuration);
        configuration["PathBase"] = PathBase;
    }
}

/// <summary>Test output that goes nowhere, for hosts created outside a test (fixtures).</summary>
internal sealed class NullTestOutput : ITestOutputHelper
{
    public void WriteLine(string message)
    {
    }

    public void WriteLine(string format, params object[] args)
    {
    }
}
