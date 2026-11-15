using ElPaqueto.EndToEnd.Tests.Harness;

namespace ElPaqueto.EndToEnd.Tests.Scenarios;

/// <summary>
/// #37 scenarios 8 to 14: duplicate versions and version selection, with
/// <c>ElPaqueto.E2E.Base</c> 1.0.0, 1.1.0 and 1.2.0-preview.1 on the server.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class DuplicateAndVersioningTests : EndToEndTest<DuplicateAndVersioningTests.Setup>, IClassFixture<DuplicateAndVersioningTests.Setup>
{
    private static readonly string[] StableVersions = ["1.0.0", "1.1.0"];
    private static readonly string[] AllVersions = ["1.0.0", "1.1.0", "1.2.0-preview.1"];

    public DuplicateAndVersioningTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Scenario08_PushOfAnExistingVersion_IsRefusedWith409_AndKeepsTheStoredPackage()
    {
        var original = Fixture.Packages.Base100;
        var variant = Fixture.Packages.Base100Variant;
        Assert.NotEqual(original.Sha512, variant.Sha512);
        var client = await ClientAsync(Fixture.Reader);

        // Same id and version, different content: an overwrite would change the stored bytes.
        var result = await client.PushAsync(variant, Fixture.PublishKey);

        result.ShouldFail().ShouldContain("409 (Conflict)");
        Assert.Equal(original.Sha512, ServerApi.Sha512(await File.ReadAllBytesAsync(Fixture.Api.StoredPackagePath(original))));
        Assert.Equal(original.Sha512, ServerApi.Sha512((await Fixture.Api.DownloadAsync(original))!));
    }

    [Fact]
    public async Task Scenario09_PushWithSkipDuplicate_ReportsTheConflictAsSkipped()
    {
        var client = await ClientAsync(Fixture.Reader);

        var result = await client.PushAsync(Fixture.Packages.Base100, Fixture.PublishKey, "--skip-duplicate");

        result.ShouldSucceed().ShouldContain("Conflict").ShouldContain("already exists at feed");
        Assert.Equal(Fixture.Packages.Base100.Sha512, ServerApi.Sha512((await Fixture.Api.DownloadAsync(Fixture.Packages.Base100))!));
    }

    [Fact]
    public async Task Scenario10_Search_ListsStableVersions_AndThePrereleaseOnlyOnRequest()
    {
        var client = await ClientAsync(Fixture.Reader);
        string[] search = ["package", "search", PackageSet.BaseId, "--source", NuGetClient.SourceName, "--configfile", client.ConfigPath, "--exact-match", "--format", "json"];

        var stable = SearchVersions((await client.DotnetAsync(client.Root, search)).ShouldSucceed());
        var all = SearchVersions((await client.DotnetAsync(client.Root, [.. search, "--prerelease"])).ShouldSucceed());

        Assert.Equal(StableVersions, stable);
        Assert.Equal(AllVersions, all);
    }

    [Fact]
    public async Task Scenario11_AddPackageWithAnExactVersion_ResolvesThatVersion()
    {
        var resolved = await AddPackageAsync("exact", "--version", "1.1.0");

        Assert.Equal("1.1.0", resolved);
    }

    [Fact]
    public async Task Scenario12_AddPackageWithoutAVersion_ResolvesTheLatestStableVersion()
    {
        var resolved = await AddPackageAsync("latest");

        Assert.Equal("1.1.0", resolved);
    }

    [Fact]
    public async Task Scenario13_AddPackageWithPrerelease_ResolvesThePrerelease()
    {
        var resolved = await AddPackageAsync("prerelease", "--prerelease");

        Assert.Equal("1.2.0-preview.1", resolved);
    }

    [Theory]
    [InlineData("1.*", "1.1.0")]
    [InlineData("1.*-*", "1.2.0-preview.1")]
    public async Task Scenario14_FloatingVersions_ResolveTheHighestMatchingVersion(string range, string expected)
    {
        var client = await ClientAsync(Fixture.Reader);
        var project = client.WriteProject("floating", [(PackageSet.BaseId, range)]);
        await client.ClearCachesAsync();

        (await client.DotnetAsync(project, ["restore", "--configfile", client.ConfigPath])).ShouldSucceed();

        Assert.Equal(expected, NuGetClient.ResolvedVersion(project, PackageSet.BaseId));
    }

    private async Task<string> AddPackageAsync(string name, params string[] options)
    {
        var client = await ClientAsync(Fixture.Reader);
        var project = client.WriteProject(name, []);
        await client.ClearCachesAsync();

        // dotnet add package has no --configfile: it reads the NuGet.Config of the client root, above the project.
        (await client.DotnetAsync(project, ["add", "package", PackageSet.BaseId, .. options])).ShouldSucceed();

        return NuGetClient.ResolvedVersion(project, PackageSet.BaseId);
    }

    private static List<string> SearchVersions(CommandResult result)
    {
        using var json = System.Text.Json.JsonDocument.Parse(result.StandardOutput);
        var packages = json.RootElement.GetProperty("searchResult").EnumerateArray()
            .SelectMany(s => s.GetProperty("packages").EnumerateArray())
            .ToList();
        Assert.All(packages, p => Assert.Equal(PackageSet.BaseId, p.GetProperty("id").GetString()));
        return packages.Select(p => p.GetProperty("version").GetString()!).Order(StringComparer.Ordinal).ToList();
    }

    public sealed class Setup : ServerFixture
    {
        protected override Task SeedAsync()
            => Api.EnsurePushedAsync(PublishKey, Packages.Base100, Packages.Base110, Packages.Base120Preview);
    }
}
