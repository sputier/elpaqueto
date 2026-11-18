using ElPaqueto.EndToEnd.Tests.Harness;

namespace ElPaqueto.EndToEnd.Tests.Clients;

/// <summary>#43: <c>nuget delete</c> (an unlist with the default <c>PackageDeletionBehavior</c>) with the real nuget.exe.</summary>
[Trait("Category", "EndToEnd")]
[Trait("Client", "NuGetExe")]
public sealed class NuGetExeDeleteTests : EndToEndTest<NuGetExeDeleteTests.Setup>, IClassFixture<NuGetExeDeleteTests.Setup>
{
    public NuGetExeDeleteTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [WindowsFact]
    public async Task Delete_WithAReadOnlyKey_IsRefusedWith403_AndThePackageStaysListed()
    {
        var package = Fixture.Packages.Base100;
        var client = await SourceClientAsync();
        var key = await Admin.CreateKeyAsync(Fixture.Publisher.UserName, UniqueName("read-only"), "read");

        var result = await NuGetExeHelpers.RunAsync(client, ["delete", package.Id, package.Version, "-Source", NuGetClient.SourceName, "-ApiKey", key.Token, "-ConfigFile", client.ConfigPath]);

        result.ShouldFail().ShouldContain("Forbidden");
        Assert.True(await Fixture.Api.IsListedAsync(package));
    }

    [WindowsFact]
    public async Task Delete_WithAPublishKey_UnlistsTheVersion_ButKeepsItDownloadable()
    {
        var package = Fixture.Packages.Base110;
        var client = await SourceClientAsync();
        Assert.True(await Fixture.Api.IsListedAsync(package));

        var result = (await NuGetExeHelpers.RunAsync(client, ["delete", package.Id, package.Version, "-Source", NuGetClient.SourceName, "-ApiKey", Fixture.PublishKey.Token, "-ConfigFile", client.ConfigPath])).ShouldSucceed();

        result.ShouldContain("was deleted successfully.");
        Assert.True(await Fixture.Api.HasVersionAsync(package));
        Assert.False(await Fixture.Api.IsListedAsync(package));
        Assert.NotNull(await Fixture.Api.DownloadAsync(package));
        var search = (await NuGetExeHelpers.RunAsync(client, ["search", package.Id, "-Source", NuGetClient.SourceName])).ShouldSucceed();
        search.ShouldNotContain("1.1.0");
    }

    private async Task<NuGetClient> SourceClientAsync()
    {
        var client = BareClient();
        await NuGetExeHelpers.LogVersionAsync(client, Output);
        (await NuGetExeHelpers.AddSourceAsync(client, Fixture.Server.ServiceIndex, Fixture.Reader, clearText: false)).ShouldSucceed();
        return client;
    }

    public sealed class Setup : ServerFixture
    {
        protected override Task SeedAsync()
            => Api.EnsurePushedAsync(PublishKey, Packages.Base100, Packages.Base110);
    }
}
