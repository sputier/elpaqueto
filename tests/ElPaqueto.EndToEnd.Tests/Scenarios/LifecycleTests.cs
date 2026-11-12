using System.Net;
using System.Text.Json;
using ElPaqueto.EndToEnd.Tests.Harness;

namespace ElPaqueto.EndToEnd.Tests.Scenarios;

/// <summary>
/// #40 scenarios 1 to 6: unlist (the default <c>PackageDeletionBehavior</c>), relist and the refusals.
/// Each test first puts <c>ElPaqueto.E2E.Base</c> 1.0.0 in the state it starts from, so the order of the
/// tests does not matter.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class UnlistTests : EndToEndTest<UnlistTests.Setup>, IClassFixture<UnlistTests.Setup>
{
    public UnlistTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private TestPackage Unlisted => Fixture.Packages.Base100;

    [Fact]
    public async Task Scenario1_DeleteWithAPublishKey_UnlistsTheVersion()
    {
        await EnsureListedAsync();
        var client = await ClientAsync(Fixture.Reader);

        (await client.DeleteAsync(Unlisted.Id, Unlisted.Version, Fixture.PublishKey)).ShouldSucceed().ShouldContain("was deleted successfully");

        Assert.False(await Fixture.Api.IsListedAsync(Unlisted));
        Assert.True(File.Exists(Fixture.Api.StoredPackagePath(Unlisted)), "Unlisting removed the package file.");
    }

    [Fact]
    public async Task Scenario2_AnUnlistedVersion_IsHiddenFromSearch_ButStillDownloadable()
    {
        await EnsureUnlistedAsync();
        var client = await ClientAsync(Fixture.Reader);

        var search = (await client.DotnetAsync(
            client.Root,
            ["package", "search", PackageSet.BaseId, "--source", NuGetClient.SourceName, "--configfile", client.ConfigPath, "--exact-match", "--format", "json"]))
            .ShouldSucceed();

        using var json = JsonDocument.Parse(search.StandardOutput);
        var versions = json.RootElement.GetProperty("searchResult").EnumerateArray()
            .SelectMany(s => s.GetProperty("packages").EnumerateArray())
            .Select(p => p.GetProperty("version").GetString())
            .ToList();
        Assert.Contains("1.1.0", versions);
        Assert.DoesNotContain("1.0.0", versions);
        Assert.Equal(Unlisted.Sha512, ServerApi.Sha512((await Fixture.Api.DownloadAsync(Unlisted))!));
    }

    [Fact]
    public async Task Scenario3_AnUnlistedVersion_IsStillRestoredOnAnExactMatch()
    {
        await EnsureUnlistedAsync();
        var client = await ClientAsync(Fixture.Reader);
        var project = client.WriteProject("exact", []);
        await client.ClearCachesAsync();

        (await client.DotnetAsync(project, ["add", "package", PackageSet.BaseId, "--version", "1.0.0"])).ShouldSucceed();

        Assert.Equal("1.0.0", NuGetClient.ResolvedVersion(project, PackageSet.BaseId));
    }

    [Fact]
    public async Task Scenario4_ADependencyRange_StillResolvesTheUnlistedLowestVersion()
    {
        await EnsureUnlistedAsync();
        var client = await ClientAsync(Fixture.Reader);
        var project = client.WriteProject("range", [(PackageSet.DependentId, "1.0.0")]);
        await client.ClearCachesAsync();

        (await client.DotnetAsync(project, ["restore", "--configfile", client.ConfigPath])).ShouldSucceed();

        // Observed with the pinned SDK: restore reads the versions from the flat container, which lists unlisted
        // versions too (as upstream and nuget.org do), and takes the lowest version of [1.0.0, ): the unlisted 1.0.0.
        // The listed flag is unchanged and still hides 1.0.0 from search (scenario 2).
        Assert.Equal("1.0.0", NuGetClient.ResolvedVersion(project, PackageSet.BaseId));
        Assert.False(await Fixture.Api.IsListedAsync(Unlisted));
    }

    [Fact]
    public async Task Scenario5_RelistWithAPublishKey_ListsTheVersionAgain()
    {
        await EnsureUnlistedAsync();

        // No dotnet command relists: POST api/v2/package/{id}/{version} with the key in X-NuGet-ApiKey.
        Assert.Equal(HttpStatusCode.OK, await Fixture.Api.RelistAsync(Fixture.PublishKey, Unlisted));

        Assert.True(await Fixture.Api.IsListedAsync(Unlisted));
    }

    [Theory]
    [InlineData("read-only key", "403 (Forbidden)")]
    [InlineData("revoked key", "401 (Unauthorized)")]
    [InlineData("no key", "403 (Forbidden)")]
    public async Task Scenario6_Delete_IsRefusedWithoutAValidPublishKey_AndTheVersionStaysListed(string refusal, string expected)
    {
        await EnsureListedAsync();
        var key = refusal switch
        {
            "read-only key" => await Admin.CreateKeyAsync(Fixture.Publisher.UserName, "read-only", "read"),
            "revoked key" => await RevokedKeyAsync(),
            _ => (ApiKey?)null,
        };
        var client = await ClientAsync(Fixture.Reader);

        (await client.DeleteAsync(Unlisted.Id, Unlisted.Version, key)).ShouldFail().ShouldContain(expected);

        Assert.True(await Fixture.Api.IsListedAsync(Unlisted));
    }

    [Fact]
    public async Task Scenario6_DeleteOfAMissingVersion_Is404OnlyForAnAuthorizedCaller()
    {
        var missing = Fixture.Api.Url($"api/v2/package/{PackageSet.BaseId}/9.9.9");
        using var authorized = await Fixture.Api.SendAsync(ServerApi.Request(HttpMethod.Delete, missing, apiKey: Fixture.PublishKey));
        using var anonymous = await Fixture.Api.SendAsync(ServerApi.Request(HttpMethod.Delete, missing));
        using var reader = await Fixture.Api.SendAsync(ServerApi.Request(HttpMethod.Delete, missing, Fixture.Reader));
        var client = await ClientAsync(Fixture.Reader);
        var cli = await client.DeleteAsync(PackageSet.BaseId, "9.9.9", Fixture.PublishKey);

        Assert.Equal(HttpStatusCode.NotFound, authorized.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);
        cli.ShouldFail().ShouldContain("404 (Not Found)");
    }

    private async Task<ApiKey> RevokedKeyAsync()
    {
        var key = await Admin.CreateKeyAsync(Fixture.Publisher.UserName, "revoked", "publish");
        await Admin.RevokeKeyAsync(key);
        return key;
    }

    private async Task EnsureListedAsync()
    {
        Assert.Equal(HttpStatusCode.OK, await Fixture.Api.RelistAsync(Fixture.PublishKey, Unlisted));
        Assert.True(await Fixture.Api.IsListedAsync(Unlisted));
    }

    private async Task EnsureUnlistedAsync()
    {
        await Fixture.Api.UnlistAsync(Fixture.PublishKey, Unlisted);
        Assert.False(await Fixture.Api.IsListedAsync(Unlisted));
    }

    public sealed class Setup : ServerFixture
    {
        protected override Task SeedAsync()
            => Api.EnsurePushedAsync(PublishKey, Packages.Base100, Packages.Base110, Packages.Base120Preview, Packages.Dependent100);
    }
}

/// <summary>#40 scenarios 7 and 8: hard delete, with <c>PackageDeletionBehavior=HardDelete</c>.</summary>
[Trait("Category", "EndToEnd")]
public sealed class HardDeleteTests : EndToEndTest<HardDeleteTests.Setup>, IClassFixture<HardDeleteTests.Setup>
{
    public HardDeleteTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Scenario7_HardDeleteWithAnAdminKey_RemovesTheVersion_AndItsRestoreFails()
    {
        var package = Fixture.Packages.Base100;
        Assert.True(File.Exists(Fixture.Api.StoredPackagePath(package)));
        var client = await ClientAsync(Fixture.Reader);

        // The server setting is honoured: a hard delete needs Admin on top of Publish (ADR 0002, #26).
        (await client.DeleteAsync(package.Id, package.Version, Fixture.AdminKey)).ShouldSucceed();

        Assert.Null(await Fixture.Api.DownloadAsync(package));
        Assert.False(await Fixture.Api.HasVersionAsync(package));
        // The version's files (.nupkg, .nuspec, readme, icon) are deleted; the inherited file storage leaves the empty folder.
        var folder = Path.GetDirectoryName(Fixture.Api.StoredPackagePath(package))!;
        Assert.True(!Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any(), "The version's files are still in the storage.");
        var project = client.WriteProject("deleted", [(PackageSet.BaseId, "[1.0.0]")]);
        await client.ClearCachesAsync();
        (await client.DotnetAsync(project, ["restore", "--configfile", client.ConfigPath])).ShouldFail().ShouldContain("NU1102");
    }

    [Fact]
    public async Task Scenario8_HardDeleteWithAPublishOnlyKey_IsRefusedWith403_AndKeepsTheFiles()
    {
        var package = Fixture.Packages.Base110;
        var client = await ClientAsync(Fixture.Reader);

        (await client.DeleteAsync(package.Id, package.Version, Fixture.PublishKey)).ShouldFail().ShouldContain("403 (Forbidden)");

        Assert.True(await Fixture.Api.IsListedAsync(package));
        Assert.True(File.Exists(Fixture.Api.StoredPackagePath(package)));
        Assert.Equal(package.Sha512, ServerApi.Sha512((await Fixture.Api.DownloadAsync(package))!));
    }

    public sealed class Setup : ServerFixture
    {
        /// <summary>A key of the administrator with <c>publish</c> and <c>admin</c>.</summary>
        internal ApiKey AdminKey { get; private set; } = null!;

        protected override void Configure(IDictionary<string, string?> settings)
            => settings["PackageDeletionBehavior"] = "HardDelete";

        protected override async Task SeedAsync()
        {
            AdminKey = await Admin.CreateKeyAsync(Administrator.UserName, "hard-delete", "publish,admin");
            await Api.EnsurePushedAsync(PublishKey, Packages.Base100, Packages.Base110);
        }
    }
}
