using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ElPaqueto.Core;
using ElPaqueto.Security;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Versioning;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// <c>IsReadOnlyMode</c> (#28, ADR 0002 decision 11): an authorized push, symbol push, delete or relist gets
/// upstream's bare 401 from the controller (no challenge, no body, no redirect) and changes nothing; an
/// unauthorized one is refused by authorization first, exactly as outside read-only mode; reads still work.
/// </summary>
public class ReadOnlyModeIntegrationTests
{
    private const string Password = "a long enough passphrase for tests";
    private static readonly NuGetVersion Version = NuGetVersion.Parse("1.2.3");

    private readonly ITestOutputHelper _output;

    public ReadOnlyModeIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static IEnumerable<object[]> Writes() =>
    [
        ["push"],
        ["symbol push"],
        ["delete"],
        ["relist"],
    ];

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task AnAuthorizedWrite_GetsABare401_AndChangesNothing(string write)
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: ReadOnly);
        await PrepareAsync(app, write);
        var before = await StateAsync(app);
        using var client = app.CreateClientWithoutRedirects();

        // The test user's key holds every permission, in X-NuGet-ApiKey: authorization succeeds.
        using var response = await SendAsync(client, write, app.TestApiKey);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("WWW-Authenticate"), "Read-only mode answers upstream's bare 401, without a challenge.");
        Assert.Null(response.Headers.Location);
        Assert.Null(response.Content.Headers.ContentType);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(before, await StateAsync(app));
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task WithoutReadOnlyMode_TheSameWriteSucceeds(string write)
    {
        // Control for the test above: the same request, the same key, read-only mode off.
        using var app = new ElPaquetoApplication(_output);
        await PrepareAsync(app, write);
        var before = await StateAsync(app);
        using var client = app.CreateClientWithoutRedirects();

        using var response = await SendAsync(client, write, app.TestApiKey);

        Assert.True(response.IsSuccessStatusCode, $"{write} answered {(int)response.StatusCode}.");
        Assert.NotEqual(before, await StateAsync(app));
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task AnUnauthorizedWrite_IsRefusedByAuthorizationFirst_AsOutsideReadOnlyMode(string write)
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: ReadOnly);
        await PrepareAsync(app, write);
        await app.AddUserAsync("reader", Password, Permission.Read);
        var readKey = await app.AddApiKeyAsync("reader", Permission.Read);
        var before = await StateAsync(app);
        using var client = app.CreateClientWithoutRedirects();

        using var anonymous = await SendAsync(client, write, apiKey: null);
        using var withoutPublish = await SendAsync(client, write, readKey.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(new[] { "Basic realm=\"ElPaqueto\"" }, anonymous.Headers.GetValues("WWW-Authenticate"));
        Assert.Equal(HttpStatusCode.Forbidden, withoutPublish.StatusCode);
        Assert.False(withoutPublish.Headers.Contains("WWW-Authenticate"));
        Assert.Equal(before, await StateAsync(app));
    }

    [Fact]
    public async Task Reads_StillWork_InReadOnlyMode()
    {
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: ReadOnly);
        await app.AddPackageAsync(TestResources.GetResourceStream(TestResources.Package));
        await app.AddUserAsync("reader", Password, Permission.Read);
        using var reader = app.CreateClientFor("reader", Password);

        using var index = await reader.GetAsync("v3/index.json");
        using var download = await reader.GetAsync("v3/package/testdata/1.2.3/testdata.1.2.3.nupkg");
        using var page = await reader.GetAsync("packages/TestData");

        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    private static void ReadOnly(Dictionary<string, string> settings) => settings["IsReadOnlyMode"] = "true";

    // What each write needs to exist before it: nothing for a push, the package for a symbol push or a delete, an
    // unlisted package for a relist.
    private static async Task PrepareAsync(ElPaquetoApplication app, string write)
    {
        if (write == "push")
        {
            return;
        }

        await app.AddPackageAsync(TestResources.GetResourceStream(TestResources.Package));
        if (write == "relist")
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IPackageDatabase>().UnlistPackageAsync("TestData", Version, CancellationToken.None);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string write, string apiKey)
    {
        using var request = write switch
        {
            "push" => new HttpRequestMessage(HttpMethod.Put, "api/v2/package") { Content = PackageContent(TestResources.Package) },
            "symbol push" => new HttpRequestMessage(HttpMethod.Put, "api/v2/symbol") { Content = PackageContent(TestResources.SymbolPackage) },
            "delete" => new HttpRequestMessage(HttpMethod.Delete, "api/v2/package/TestData/1.2.3"),
            "relist" => new HttpRequestMessage(HttpMethod.Post, "api/v2/package/TestData/1.2.3") { Content = new ByteArrayContent([]) },
            _ => throw new ArgumentOutOfRangeException(nameof(write), write, null),
        };

        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation(TestUserExtensions.ApiKeyHeader, apiKey);
        }

        return await client.SendAsync(request);
    }

    private static MultipartFormDataContent PackageContent(string resource)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(TestResources.GetResourceStream(resource).ToArray()), "package", "package.nupkg");
        return content;
    }

    // What a write could change: whether the package exists, whether it is listed, whether it has symbols.
    private static async Task<string> StateAsync(ElPaquetoApplication app)
    {
        using var scope = app.Services.CreateScope();
        var package = await scope.ServiceProvider.GetRequiredService<IPackageDatabase>()
            .FindOrNullAsync("TestData", Version, includeUnlisted: true, CancellationToken.None);
        var symbols = await scope.ServiceProvider.GetRequiredService<ISymbolStorageService>()
            .GetPortablePdbContentStreamOrNullAsync("testdata.pdb", "16F71ED8DD574AA2AD4A22D29E9C981Bffffffff");
        var hasSymbols = symbols is not null;
        if (symbols is not null)
        {
            await symbols.DisposeAsync();
        }

        return package is null ? $"absent, symbols: {hasSymbols}" : $"present, listed: {package.Listed}, symbols: {hasSymbols}";
    }
}
