using ElPaqueto.EndToEnd.Tests.Harness;

namespace ElPaqueto.EndToEnd.Tests.Clients;

/// <summary>
/// #43: what Visual Studio does when it restores. The IDE (Package Manager UI, build, "Restore NuGet Packages")
/// runs NuGet's MSBuild restore, <c>msbuild -t:restore</c>, with the credentials of <c>NuGet.Config</c>
/// (<c>packageSourceCredentials</c>, the password encrypted with DPAPI as the IDE writes it). This is automated
/// with <c>dotnet msbuild</c> and, when a Visual Studio is installed, with its own <c>MSBuild.exe</c>. The IDE
/// itself is not driven: see docs/user/client-checklist.md.
/// </summary>
[Trait("Category", "EndToEnd")]
[Trait("Client", "MSBuild")]
public sealed class MSBuildRestoreTests : EndToEndTest<MSBuildRestoreTests.Setup>, IClassFixture<MSBuildRestoreTests.Setup>
{
    public MSBuildRestoreTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task DotnetMSBuildRestore_WithClearTextCredentialsInNuGetConfig_ResolvesThePackagesFromTheServer()
    {
        // Runs on every OS: the same restore, with the credentials in packageSourceCredentials as clear text.
        var client = await ClientAsync(Fixture.Reader);
        var project = client.WriteProject("app-clear", [(PackageSet.DependentId, "1.0.0")]);
        await client.ClearCachesAsync();

        (await client.DotnetAsync(project, ["msbuild", "-t:restore", "-nologo", "-v:minimal", "app-clear.csproj"])).ShouldSucceed();

        Assert.Equal("1.0.0", NuGetClient.ResolvedVersion(project, PackageSet.BaseId));
        Assert.Equal("1.0.0", NuGetClient.ResolvedVersion(project, PackageSet.DependentId));
    }

    [WindowsFact]
    public async Task DotnetMSBuildRestore_WithAPasswordEncryptedByNuGetExe_ResolvesThePackagesFromTheServer()
    {
        // What Visual Studio (and nuget.exe) write on Windows: Password encrypted with DPAPI, no ClearTextPassword.
        var client = await EncryptedClientAsync();
        var project = client.WriteProject("app-dpapi", [(PackageSet.DependentId, "1.0.0")]);
        await client.ClearCachesAsync();
        NuGetExeHelpers.AssertConfig(await File.ReadAllTextAsync(client.ConfigPath), "ClearTextPassword", false);

        (await client.DotnetAsync(project, ["msbuild", "-t:restore", "-nologo", "-v:minimal", "app-dpapi.csproj"])).ShouldSucceed();

        Assert.Equal("1.0.0", NuGetClient.ResolvedVersion(project, PackageSet.BaseId));
    }

    [VisualStudioFact]
    public async Task VisualStudioMSBuildRestore_WithAPasswordEncryptedByNuGetExe_ResolvesThePackagesFromTheServer()
    {
        var msbuild = VisualStudioMSBuild.Path!;
        Output.WriteLine("Visual Studio MSBuild: " + msbuild);
        var client = await EncryptedClientAsync();
        var project = client.WriteProject("app-vs", [(PackageSet.DependentId, "1.0.0")]);
        await client.ClearCachesAsync();

        var result = (await client.RunAsync(msbuild, project, ["-t:restore", "-nologo", "-v:minimal", "-nodeReuse:false", "app-vs.csproj"])).ShouldSucceed();

        Assert.Equal("1.0.0", NuGetClient.ResolvedVersion(project, PackageSet.BaseId));
        result.ShouldNotContain("NU1301");
    }

    [Fact]
    public async Task DotnetMSBuildRestore_WithoutCredentials_FailsWithNU1301And401_WithoutPrompting()
    {
        var client = await ClientAsync(sourceCredentials: null);
        var project = client.WriteProject("app-anon", [(PackageSet.BaseId, "1.0.0")]);
        await client.ClearCachesAsync();

        var result = await client.DotnetAsync(project, ["msbuild", "-t:restore", "-nologo", "-v:minimal", "app-anon.csproj"]);

        result.ShouldFail().ShouldContain("NU1301").ShouldContain("401");
        Assert.False(result.TimedOut);
    }

    [VisualStudioFact]
    public async Task VisualStudioMSBuildRestore_WithoutCredentials_FailsWithNU1301And401_WithoutPrompting()
    {
        var client = await ClientAsync(sourceCredentials: null);
        var project = client.WriteProject("app-vs-anon", [(PackageSet.BaseId, "1.0.0")]);
        await client.ClearCachesAsync();

        var result = await client.RunAsync(VisualStudioMSBuild.Path!, project, ["-t:restore", "-nologo", "-v:minimal", "-nodeReuse:false", "app-vs-anon.csproj"]);

        result.ShouldFail().ShouldContain("NU1301").ShouldContain("401");
        Assert.False(result.TimedOut);
    }

    private async Task<NuGetClient> EncryptedClientAsync()
    {
        var client = BareClient();
        (await NuGetExeHelpers.AddSourceAsync(client, Fixture.Server.ServiceIndex, Fixture.Reader, clearText: false)).ShouldSucceed();
        return client;
    }

    public sealed class Setup : ServerFixture
    {
        protected override Task SeedAsync()
            => Api.EnsurePushedAsync(PublishKey, Packages.Base100, Packages.Dependent100);
    }
}
