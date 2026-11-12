using System.Text.RegularExpressions;
using ElPaqueto.EndToEnd.Tests.Harness;

namespace ElPaqueto.EndToEnd.Tests.Scenarios;

/// <summary>
/// #38: the developer loop with cleared caches. <c>ElPaqueto.E2E.Base</c> 1.0.0, 1.1.0, 1.2.0-preview.1 and
/// <c>ElPaqueto.E2E.Dependent</c> 1.0.0 (which depends on Base <c>[1.0.0, )</c>) are on the server.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed partial class ConsumeTests : EndToEndTest<ConsumeTests.Setup>, IClassFixture<ConsumeTests.Setup>
{
    // The versions the repository's own tests use (Directory.Packages.props).
    private const string TestSdkVersion = "17.13.0";
    private const string XunitVersion = "2.9.3";
    private const string XunitRunnerVersion = "3.0.2";

    private const string UsesBase = """
        namespace Consumer;

        public static class UsesBase
        {
            public static string Describe() => ElPaqueto.E2E.Base.BaseInfo.Describe();
        }
        """;

    public ConsumeTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Step1_AddPackage_WritesThePackageReference()
    {
        var client = await ClientAsync(Fixture.Reader);
        var project = client.WriteProject("add", []);

        (await client.DotnetAsync(project, ["add", "package", PackageSet.DependentId, "--version", "1.0.0"])).ShouldSucceed();

        var projectFile = await File.ReadAllTextAsync(Path.Combine(project, "add.csproj"));
        Assert.Contains($"<PackageReference Include=\"{PackageSet.DependentId}\" Version=\"1.0.0\" />", projectFile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Step2_Restore_WithClearedCaches_ResolvesTheTransitiveDependencyFromTheServer()
    {
        var client = await ClientAsync(Fixture.Reader);
        var project = client.WriteProject("restore", [(PackageSet.DependentId, "1.0.0")]);
        await client.ClearCachesAsync();

        (await client.DotnetAsync(project, ["restore", "--configfile", client.ConfigPath])).ShouldSucceed();

        Assert.Equal("1.0.0", NuGetClient.ResolvedVersion(project, PackageSet.DependentId));
        // NuGet picks the lowest version of the range [1.0.0, ).
        Assert.Equal("1.0.0", NuGetClient.ResolvedVersion(project, PackageSet.BaseId));
        Assert.True(File.Exists(client.GlobalPackagePath(PackageSet.DependentId, "1.0.0")));
        Assert.True(File.Exists(client.GlobalPackagePath(PackageSet.BaseId, "1.0.0")));
    }

    [Fact]
    public async Task Step3_ASecondRestoreAfterAnotherClear_DownloadsFromTheServerAgain()
    {
        var client = await ClientAsync(Fixture.Reader);
        var project = client.WriteProject("again", [(PackageSet.DependentId, "1.0.0")]);
        await client.ClearCachesAsync();
        (await client.DotnetAsync(project, ["restore", "--configfile", client.ConfigPath])).ShouldSucceed();
        var dependentDownloads = await Fixture.Api.DownloadsAsync(Fixture.Packages.Dependent100);
        var baseDownloads = await Fixture.Api.DownloadsAsync(Fixture.Packages.Base100);

        await client.ClearCachesAsync();
        (await client.DotnetAsync(project, ["restore", "--force", "--configfile", client.ConfigPath])).ShouldSucceed();

        Assert.True(File.Exists(client.GlobalPackagePath(PackageSet.DependentId, "1.0.0")));
        Assert.Equal(dependentDownloads + 1, await Fixture.Api.DownloadsAsync(Fixture.Packages.Dependent100));
        Assert.Equal(baseDownloads + 1, await Fixture.Api.DownloadsAsync(Fixture.Packages.Base100));
    }

    [Fact]
    public async Task Step4_Build_OfCodeThatUsesAPackageType_Succeeds()
    {
        var client = await ClientAsync(Fixture.Reader);
        var project = client.WriteProject("build", [(PackageSet.DependentId, "1.0.0")], UsesBase);
        await client.ClearCachesAsync();
        (await client.DotnetAsync(project, ["restore", "--configfile", client.ConfigPath])).ShouldSucceed();

        (await client.DotnetAsync(project, ["build", "--no-restore"])).ShouldSucceed();

        Assert.True(File.Exists(Path.Combine(project, "bin", "Debug", "net10.0", "build.dll")));
    }

    [Fact]
    public async Task Step5_Test_OfAnXunitProjectUsingThePackage_PassesWithSourceMapping()
    {
        var client = BareClient();
        await client.LogToolVersionsAsync();
        // Two sources: ElPaqueto's packages may only come from ElPaqueto, everything else from nuget.org.
        client.WriteConfig($"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="{NuGetClient.SourceName}" value="{Fixture.Server.ServiceIndex.AbsoluteUri}" allowInsecureConnections="true" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="{NuGetClient.SourceName}">
                  <package pattern="ElPaqueto.E2E.*" />
                </packageSource>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
              <packageSourceCredentials>
                <{NuGetClient.SourceName}>
                  <add key="Username" value="{Fixture.Reader.UserName}" />
                  <add key="ClearTextPassword" value="{Fixture.Reader.Password}" />
                </{NuGetClient.SourceName}>
              </packageSourceCredentials>
            </configuration>
            """);
        var project = client.CreateWorkDirectory("tests");
        await File.WriteAllTextAsync(Path.Combine(project, "tests.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="{TestSdkVersion}" />
                <PackageReference Include="xunit" Version="{XunitVersion}" />
                <PackageReference Include="xunit.runner.visualstudio" Version="{XunitRunnerVersion}" />
                <PackageReference Include="{PackageSet.DependentId}" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(project, "GreeterTests.cs"), """
            namespace Consumer.Tests;

            public class GreeterTests
            {
                [Xunit.Fact]
                public void Greet_UsesTheBasePackage()
                    => Xunit.Assert.Equal("Hello e2e from ElPaqueto.E2E.Base 1.0.0", ElPaqueto.E2E.Dependent.Greeter.Greet("e2e"));
            }
            """);
        await client.ClearCachesAsync();

        // Detailed verbosity lists every HTTP request, so that the test can see where each package came from.
        var restore = (await client.DotnetAsync(project, ["restore", "--configfile", client.ConfigPath, "--verbosity", "detailed"], TimeSpan.FromMinutes(8)))
            .ShouldSucceed();
        var test = await client.DotnetAsync(project, ["test", "--no-restore"], TimeSpan.FromMinutes(8));

        test.ShouldSucceed();
        Assert.True(PassedOne().IsMatch(test.Output), test.Describe());
        var requests = HttpGets().Matches(restore.Output).Select(m => m.Groups[1].Value).ToList();
        Assert.Contains(requests, url => url.StartsWith(Fixture.Server.BaseAddress.AbsoluteUri, StringComparison.Ordinal) && url.Contains("elpaqueto.e2e.dependent", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requests, url => url.StartsWith("https://api.nuget.org/", StringComparison.Ordinal) && url.Contains("xunit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(requests, url => url.StartsWith("https://api.nuget.org/", StringComparison.Ordinal) && url.Contains("elpaqueto", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(requests, url => url.StartsWith(Fixture.Server.BaseAddress.AbsoluteUri, StringComparison.Ordinal) && url.Contains("xunit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Step6_Restore_WithoutCredentials_FailsWith401_WithoutPrompting()
    {
        var client = await ClientAsync(sourceCredentials: null);
        var project = client.WriteProject("anonymous", [(PackageSet.DependentId, "1.0.0")]);
        await client.ClearCachesAsync();

        var result = await client.DotnetAsync(project, ["restore", "--configfile", client.ConfigPath], TimeSpan.FromMinutes(2));

        result.ShouldFail().ShouldContain("NU1301").ShouldContain("401 (Unauthorized)");
        Assert.False(File.Exists(client.GlobalPackagePath(PackageSet.DependentId, "1.0.0")));
    }

    [GeneratedRegex(@"Passed:\s+1\b")]
    private static partial Regex PassedOne();

    [GeneratedRegex(@"GET (https?://\S+)")]
    private static partial Regex HttpGets();

    public sealed class Setup : ServerFixture
    {
        protected override Task SeedAsync()
            => Api.EnsurePushedAsync(PublishKey, Packages.Base100, Packages.Base110, Packages.Base120Preview, Packages.Dependent100);
    }
}
