using System.Text.Json;
using ElPaqueto.EndToEnd.Tests.Harness;

namespace ElPaqueto.EndToEnd.Tests.Scenarios;

/// <summary>#37 scenarios 1 to 7: source configuration and publishing with the real <c>dotnet</c> CLI.</summary>
[Trait("Category", "EndToEnd")]
public sealed class SourceAndPublishTests : EndToEndTest<SourceAndPublishTests.Setup>, IClassFixture<SourceAndPublishTests.Setup>
{
    public SourceAndPublishTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    public static TheoryData<string, int> RefusedPushCases => RefusedPushes.Cases;

    [Fact]
    public async Task Scenario01_AddSource_WithCredentialsAndInsecureConnectionsAllowed_Succeeds()
    {
        var client = BareClient();

        (await client.AddSourceAsync(Fixture.Server.ServiceIndex, Fixture.Reader)).ShouldSucceed();

        var config = await File.ReadAllTextAsync(client.ConfigPath);
        Assert.Contains($"value=\"{Fixture.Server.ServiceIndex.AbsoluteUri}\"", config, StringComparison.Ordinal);
        Assert.Contains("allowInsecureConnections=\"True\"", config, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<add key=\"Username\" value=\"reader\" />", config, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scenario02_ListSource_ShowsTheSourceEnabled_AndNeverThePassword()
    {
        var client = await ClientAsync(Fixture.Reader);

        var result = (await client.DotnetAsync(client.Root, ["nuget", "list", "source", "--configfile", client.ConfigPath])).ShouldSucceed();

        result.ShouldContain($"{NuGetClient.SourceName} [Enabled]").ShouldContain(Fixture.Server.ServiceIndex.AbsoluteUri);
        result.ShouldNotContain(Fixture.Reader.Password);
    }

    [Fact]
    public async Task Scenario03_CredentialsFromTheEnvironment_AuthenticateASearch()
    {
        var client = await ClientAsync(sourceCredentials: null);
        string[] search = ["package", "search", PackageSet.DependentId, "--source", NuGetClient.SourceName, "--configfile", client.ConfigPath, "--format", "json"];

        // The CI pattern: no secret in NuGet.Config, the credentials come from the environment.
        var credentials = new Dictionary<string, string?>
        {
            [$"NuGetPackageSourceCredentials_{NuGetClient.SourceName}"] = $"Username={Fixture.Reader.UserName};Password={Fixture.Reader.Password}",
        };
        var authenticated = (await client.DotnetAsync(client.Root, search, extraEnvironment: credentials)).ShouldSucceed();
        await client.ClearCachesAsync();
        var logStart = Fixture.Server.Log.Length;
        var withoutCredentials = (await client.DotnetAsync(client.Root, search)).ShouldSucceed();

        Assert.Empty(Problems(authenticated));
        Assert.Contains(PackageSet.DependentId, Packages(authenticated));
        // dotnet package search exits with 0 even when the source refuses it: the refusal is a "problem" in the JSON,
        // without the status code, which only the server log shows.
        Assert.NotEmpty(Problems(withoutCredentials));
        Assert.Contains("/v3/index.json - 401", Fixture.Server.Log[logStart..], StringComparison.Ordinal);
        Assert.DoesNotContain("/v3/index.json - 200", Fixture.Server.Log[logStart..], StringComparison.Ordinal);
        Assert.Empty(Packages(withoutCredentials));
    }

    [Fact]
    public async Task Scenario04_Push_WithAPublishKey_PublishesThePackage()
    {
        var package = Fixture.Packages.Base100;
        var client = await ClientAsync(Fixture.Reader);

        (await client.PushAsync(package, Fixture.PublishKey)).ShouldSucceed().ShouldContain("Your package was pushed.");

        Assert.True(await Fixture.Api.IsListedAsync(package));
        Assert.Equal(package.Sha512, ServerApi.Sha512(await File.ReadAllBytesAsync(Fixture.Api.StoredPackagePath(package))));
        Assert.Equal(package.Sha512, ServerApi.Sha512((await Fixture.Api.DownloadAsync(package))!));
    }

    [Fact]
    public async Task Scenario05_Push_WithASymbolPackage_PublishesTheSymbolsThroughSymbolPackagePublish()
    {
        var package = Fixture.Packages.Symbols100;
        Assert.True(File.Exists(Path.ChangeExtension(package.Path, ".snupkg")));
        var client = await ClientAsync(Fixture.Reader);

        var result = (await client.PushAsync(package, Fixture.PublishKey)).ShouldSucceed();

        // The symbol package goes to the SymbolPackagePublish/4.9.0 resource of the service index.
        result.ShouldContain($"Pushing {PackageSet.SymbolsId}.1.0.0.snupkg to '{new Uri(Fixture.Server.BaseAddress, "api/v2/symbol")}'")
            .ShouldContain($"Created {new Uri(Fixture.Server.BaseAddress, "api/v2/symbol/")}");
        Assert.True(await Fixture.Api.HasVersionAsync(package));
        var symbols = Path.Combine(Fixture.Server.StorageDirectory, "symbols");
        Assert.True(
            Directory.Exists(Path.Combine(symbols, PackageSet.SymbolsId.ToLowerInvariant() + ".pdb")),
            "The portable PDB of the symbol package is not in the symbol storage.");
    }

    [Theory]
    [MemberData(nameof(RefusedPushCases))]
    public async Task Scenario06_Push_IsRefused_AndChangesNothing(string refusal, int expectedStatus)
    {
        var client = await ClientAsync(Fixture.Reader);

        await RefusedPushes.AssertRefusedAsync(Fixture, Admin, client, refusal, expectedStatus, Fixture.Packages.Base110);
    }

    [Fact]
    public async Task Scenario07_Push_WithBasicCredentialsHoldingPublish_AndNoApiKey_IsRefusedWith403()
    {
        // ADR 0002 decision 6: a write needs a Publish key in X-NuGet-ApiKey. The source credentials of a user
        // holding Publish are not enough.
        var package = Fixture.Packages.Base120Preview;
        var client = await ClientAsync(Fixture.Publisher);

        var result = await client.PushAsync(package, apiKey: null);

        result.ShouldFail().ShouldContain("No API Key was provided").ShouldContain("403 (Forbidden)");
        Assert.False(await Fixture.Api.HasVersionAsync(package));
        Assert.False(File.Exists(Fixture.Api.StoredPackagePath(package)));
    }

    private static List<string> Problems(CommandResult result)
    {
        using var json = JsonDocument.Parse(result.StandardOutput);
        return json.RootElement.GetProperty("problems").EnumerateArray()
            .Concat(json.RootElement.GetProperty("searchResult").EnumerateArray()
                .SelectMany(s => s.TryGetProperty("problems", out var p) ? p.EnumerateArray() : Enumerable.Empty<JsonElement>()))
            .Select(p => p.GetProperty("text").GetString()!)
            .ToList();
    }

    private static List<string> Packages(CommandResult result)
    {
        using var json = JsonDocument.Parse(result.StandardOutput);
        return json.RootElement.GetProperty("searchResult").EnumerateArray()
            .SelectMany(s => s.GetProperty("packages").EnumerateArray())
            .Select(p => p.GetProperty("id").GetString()!)
            .ToList();
    }

    public sealed class Setup : ServerFixture
    {
        protected override Task SeedAsync()
            => Api.EnsurePushedAsync(PublishKey, Packages.Dependent100);
    }
}
