using ElPaqueto.EndToEnd.Tests.Harness;
using ElPaqueto.EndToEnd.Tests.Scenarios;

namespace ElPaqueto.EndToEnd.Tests.Clients;

/// <summary>
/// #43: the real nuget.exe (pinned release, SHA-256 checked, see <see cref="NuGetExe"/>) against the published
/// server, in an isolated environment: its own <c>%APPDATA%</c>, <c>NUGET_PACKAGES</c>, HTTP cache and
/// <c>NuGet.Config</c>, <c>-NonInteractive</c> and <c>-ForceEnglishOutput</c> on every command, a timeout on each.
/// Windows only (the tests are skipped elsewhere, with the reason).
/// </summary>
[Trait("Category", "EndToEnd")]
[Trait("Client", "NuGetExe")]
public sealed class NuGetExeTests : EndToEndTest<NuGetExeTests.Setup>, IClassFixture<NuGetExeTests.Setup>
{
    public NuGetExeTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    public static TheoryData<string, int> RefusedPushCases => new()
    {
        { RefusedPushes.InvalidKey, 401 },
        { RefusedPushes.RevokedKey, 401 },
        { RefusedPushes.ReadOnlyKey, 403 },
        { RefusedPushes.NoKey, 403 },
    };

    [WindowsFact]
    public async Task Scenario01_SourcesAdd_WithCredentials_EncryptsThePasswordWithDpapi_AndTheSourceWorks()
    {
        var client = BareClient();
        await NuGetExeHelpers.LogVersionAsync(client, Output);

        var add = (await NuGetExeHelpers.AddSourceAsync(client, Fixture.Server.ServiceIndex, Fixture.Reader, clearText: false)).ShouldSucceed();

        // The password is encrypted for the current Windows user (DPAPI): the file holds Password, never ClearTextPassword.
        var config = await File.ReadAllTextAsync(client.ConfigPath);
        NuGetExeHelpers.AssertConfig(config, "<add key=\"Username\" value=\"reader\" />", true);
        NuGetExeHelpers.AssertConfig(config, "<add key=\"Password\" value=\"", true);
        NuGetExeHelpers.AssertConfig(config, "ClearTextPassword", false);
        NuGetExeHelpers.AssertConfig(config, Fixture.Reader.Password, false);
        add.ShouldNotContain(Fixture.Reader.Password);

        var list = (await NuGetExeHelpers.RunAsync(client, ["sources", "list", "-ConfigFile", client.ConfigPath])).ShouldSucceed();
        list.ShouldContain($"{NuGetClient.SourceName} [Enabled]").ShouldContain(Fixture.Server.ServiceIndex.AbsoluteUri).ShouldNotContain(Fixture.Reader.Password);

        // The encrypted password is read back by nuget.exe itself.
        var packages = (await NuGetExeHelpers.RunAsync(client, ["search", PackageSet.BaseId, "-Source", NuGetClient.SourceName])).ShouldSucceed();
        packages.ShouldContain(PackageSet.BaseId).ShouldContain("1.1.0");
    }

    [WindowsFact]
    public async Task Scenario02_SourcesAdd_WithStorePasswordInClearText_WritesClearTextPassword_AndTheSourceWorks()
    {
        var client = BareClient();

        (await NuGetExeHelpers.AddSourceAsync(client, Fixture.Server.ServiceIndex, Fixture.Reader, clearText: true)).ShouldSucceed();

        var config = await File.ReadAllTextAsync(client.ConfigPath);
        NuGetExeHelpers.AssertConfig(config, "<add key=\"ClearTextPassword\" value=\"" + Fixture.Reader.Password + "\" />", true);
        var list = (await NuGetExeHelpers.RunAsync(client, ["sources", "list", "-ConfigFile", client.ConfigPath])).ShouldSucceed();
        list.ShouldNotContain(Fixture.Reader.Password);
        (await NuGetExeHelpers.RunAsync(client, ["search", PackageSet.BaseId, "-Source", NuGetClient.SourceName])).ShouldSucceed();
    }

    [WindowsFact]
    public async Task Scenario03_Push_WithAPublishKey_PublishesThePackageAndItsSymbols()
    {
        var package = Fixture.Packages.Symbols100;
        var client = await SourceClientAsync(Fixture.Reader);

        var result = (await NuGetExeHelpers.RunAsync(client, ["push", package.Path, "-Source", NuGetClient.SourceName, "-ApiKey", Fixture.PublishKey.Token, "-ConfigFile", client.ConfigPath])).ShouldSucceed();

        result.ShouldContain("Your package was pushed.");
        Assert.True(await Fixture.Api.IsListedAsync(package));
        Assert.Equal(package.Sha512, ServerApi.Sha512(await File.ReadAllBytesAsync(Fixture.Api.StoredPackagePath(package))));
    }

    [WindowsTheory]
    [MemberData(nameof(RefusedPushCases))]
    public async Task Scenario04_Push_IsRefused_AndChangesNothing(string refusal, int expectedStatus)
    {
        var package = Fixture.Packages.Base200;
        var client = await SourceClientAsync(Fixture.Reader);
        Assert.False(await Fixture.Api.HasVersionAsync(package));
        var key = await PrepareKeyAsync(refusal);
        List<string> arguments = ["push", package.Path, "-Source", NuGetClient.SourceName, "-ConfigFile", client.ConfigPath];
        if (key is not null)
        {
            arguments.AddRange(["-ApiKey", key.Token]);
        }

        var result = await NuGetExeHelpers.RunAsync(client, arguments);

        result.ShouldFail().ShouldContain(expectedStatus == 401 ? "Unauthorized" : "Forbidden");
        Assert.False(await Fixture.Api.HasVersionAsync(package));
        Assert.False(File.Exists(Fixture.Api.StoredPackagePath(package)));
    }

    [WindowsFact]
    public async Task Scenario05_Push_OfAnExistingVersion_Fails409_AndSkipDuplicateExitsZero()
    {
        var client = await SourceClientAsync(Fixture.Reader);
        string[] push = ["push", Fixture.Packages.Base100Variant.Path, "-Source", NuGetClient.SourceName, "-ApiKey", Fixture.PublishKey.Token, "-ConfigFile", client.ConfigPath];
        var before = Fixture.Api.StorageHashes();

        (await NuGetExeHelpers.RunAsync(client, push)).ShouldFail().ShouldContain("Conflict");
        (await NuGetExeHelpers.RunAsync(client, [.. push, "-SkipDuplicate"])).ShouldSucceed().ShouldContain("already exists");

        Assert.Equal(before, Fixture.Api.StorageHashes());
    }

    [WindowsFact]
    public async Task Scenario06_Search_ReturnsTheLatestVersion_AndPrereleaseOnlyWhenAsked()
    {
        var client = await SourceClientAsync(Fixture.Reader);
        var source = new[] { "-Source", NuGetClient.SourceName };

        // nuget.exe 6 and later: "search" (no -ConfigFile: it reads the hierarchy from the working directory, which finds the isolated file).
        var search = (await NuGetExeHelpers.RunAsync(client, ["search", PackageSet.BaseId, .. source])).ShouldSucceed();
        var searchPrerelease = (await NuGetExeHelpers.RunAsync(client, ["search", PackageSet.BaseId, "-PreRelease", .. source])).ShouldSucceed();

        search.ShouldContain(PackageSet.BaseId).ShouldNotContain("1.2.0-preview.1");
        searchPrerelease.ShouldContain("1.2.0-preview.1");
        search.ShouldContain("1.1.0");
    }

    [WindowsFact]
    public async Task Scenario06b_List_IsDeprecated_AndDoesNotListAV3Source()
    {
        // Observed with nuget.exe 7.9.0: "list" warns that it is deprecated, says it cannot list a V3 source and
        // finds nothing, with exit code 0. Scripts must use "search" (or "dotnet package search").
        var client = await SourceClientAsync(Fixture.Reader);

        var result = (await NuGetExeHelpers.RunAsync(client, ["list", PackageSet.BaseId, "-Source", NuGetClient.SourceName, "-ConfigFile", client.ConfigPath])).ShouldSucceed();

        result.ShouldContain("deprecated").ShouldContain("does not support listing packages").ShouldContain("No packages found.");
    }

    [WindowsFact]
    public async Task Scenario07_Install_ResolvesThePackageAndItsDependency()
    {
        var client = await SourceClientAsync(Fixture.Reader);
        var output = client.CreateWorkDirectory("install-out");

        var result = (await NuGetExeHelpers.RunAsync(client, ["install", PackageSet.DependentId, "-Version", "1.0.0", "-OutputDirectory", output, "-Source", NuGetClient.SourceName, "-ConfigFile", client.ConfigPath])).ShouldSucceed();

        result.ShouldContain($"Successfully installed '{PackageSet.DependentId} 1.0.0'");
        Assert.True(File.Exists(Path.Combine(output, $"{PackageSet.DependentId}.1.0.0", $"{PackageSet.DependentId}.1.0.0.nupkg")));
        Assert.True(File.Exists(Path.Combine(output, $"{PackageSet.BaseId}.1.0.0", $"{PackageSet.BaseId}.1.0.0.nupkg")));
    }

    [WindowsFact]
    public async Task Scenario08_Restore_OfAPackagesConfigProject_DownloadsThePackages_AfterClearingTheCaches()
    {
        var client = await SourceClientAsync(Fixture.Reader);
        var project = client.CreateWorkDirectory("legacy");
        var packages = Path.Combine(project, "packages");
        await File.WriteAllTextAsync(Path.Combine(project, "packages.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="{PackageSet.DependentId}" version="1.0.0" targetFramework="net48" />
              <package id="{PackageSet.BaseId}" version="1.0.0" targetFramework="net48" />
            </packages>
            """);
        (await NuGetExeHelpers.RunAsync(client, ["locals", "all", "-Clear"])).ShouldSucceed();
        var logStart = Fixture.Server.Log.Length;

        var result = (await NuGetExeHelpers.RunAsync(client, ["restore", Path.Combine(project, "packages.config"), "-PackagesDirectory", packages, "-Source", NuGetClient.SourceName, "-ConfigFile", client.ConfigPath], project)).ShouldSucceed();

        // nuget.exe restores the dependency from the packages.config too, and also fills the global packages folder.
        result.ShouldContain($"Restoring NuGet package {PackageSet.BaseId}.1.0.0.")
            .ShouldContain($"Added package '{PackageSet.DependentId}.1.0.0' to folder");
        Assert.True(File.Exists(Path.Combine(packages, $"{PackageSet.BaseId}.1.0.0", $"{PackageSet.BaseId}.1.0.0.nupkg")));
        Assert.True(File.Exists(Path.Combine(packages, $"{PackageSet.DependentId}.1.0.0", $"{PackageSet.DependentId}.1.0.0.nupkg")));
        Assert.Contains("/v3/package/", Fixture.Server.Log[logStart..], StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task Scenario09_WithoutCredentials_RestoreAndList_FailWith401_AndDoNotPrompt()
    {
        var client = await SourceClientAsync(sourceCredentials: null);
        var packagesConfig = Path.Combine(client.CreateWorkDirectory("nocred"), "packages.config");
        await File.WriteAllTextAsync(packagesConfig, $"""<packages><package id="{PackageSet.BaseId}" version="1.0.0" targetFramework="net48" /></packages>""");
        (await NuGetExeHelpers.RunAsync(client, ["locals", "all", "-Clear"])).ShouldSucceed();

        var restore = await NuGetExeHelpers.RunAsync(client, ["restore", packagesConfig, "-PackagesDirectory", Path.Combine(client.Root, "p"), "-Source", NuGetClient.SourceName, "-ConfigFile", client.ConfigPath]);
        var list = await NuGetExeHelpers.RunAsync(client, ["search", PackageSet.BaseId, "-Source", NuGetClient.SourceName]);
        // Without -NonInteractive nuget.exe would ask on the console: standard input is closed, so it cannot be answered.
        var interactive = await NuGetExeHelpers.RunRawAsync(client, ["search", PackageSet.BaseId, "-Source", NuGetClient.SourceName], timeout: TimeSpan.FromMinutes(2));

        restore.ShouldFail().ShouldContain("401");
        list.ShouldFail().ShouldContain("401");
        interactive.ShouldFail();
        Assert.False(interactive.TimedOut);
    }

    [WindowsFact]
    public async Task Scenario10_WithAWrongPassword_ListFailsWith401_AndTheRightPasswordWorksRightAfter()
    {
        var client = await SourceClientAsync(new UserCredential(Fixture.Reader.UserName, Secrets.Register("wrong-" + AdminCli.NewPassword())));
        string[] list = ["search", PackageSet.BaseId, "-Source", NuGetClient.SourceName];

        (await NuGetExeHelpers.RunAsync(client, list)).ShouldFail().ShouldContain("401");
        (await NuGetExeHelpers.RunAsync(client, ["sources", "update", "-Name", NuGetClient.SourceName, "-Username", Fixture.Reader.UserName, "-Password", Fixture.Reader.Password, "-AllowInsecureConnections", "-ConfigFile", client.ConfigPath])).ShouldSucceed();

        (await NuGetExeHelpers.RunAsync(client, list)).ShouldSucceed().ShouldContain(PackageSet.BaseId);
    }

    [WindowsFact]
    public async Task Scenario11_CredentialsFromTheEnvironment_AuthenticateASearch_AndNothingIsStoredInTheConfig()
    {
        var client = await SourceClientAsync(sourceCredentials: null);
        string[] search = ["search", PackageSet.BaseId, "-Source", NuGetClient.SourceName];
        var credentials = new Dictionary<string, string?>
        {
            [$"NuGetPackageSourceCredentials_{NuGetClient.SourceName}"] = $"Username={Fixture.Reader.UserName};Password={Fixture.Reader.Password}",
        };

        var authenticated = (await NuGetExeHelpers.RunAsync(client, search, environment: credentials)).ShouldSucceed();
        var anonymous = await NuGetExeHelpers.RunAsync(client, search);

        authenticated.ShouldContain(PackageSet.BaseId);
        anonymous.ShouldFail().ShouldContain("401");
        NuGetExeHelpers.AssertConfig(await File.ReadAllTextAsync(client.ConfigPath), Fixture.Reader.Password, false);
    }

    private async Task<NuGetClient> SourceClientAsync(UserCredential? sourceCredentials)
    {
        var client = BareClient();
        await NuGetExeHelpers.LogVersionAsync(client, Output);
        (await NuGetExeHelpers.AddSourceAsync(client, Fixture.Server.ServiceIndex, sourceCredentials, clearText: false)).ShouldSucceed();
        return client;
    }

    private async Task<ApiKey?> PrepareKeyAsync(string refusal)
    {
        switch (refusal)
        {
            case RefusedPushes.InvalidKey:
                return new ApiKey(Secrets.Register("epq_AAAAAAAAAAAA_" + new string('0', 43)));
            case RefusedPushes.RevokedKey:
                {
                    var key = await Admin.CreateKeyAsync(Fixture.Publisher.UserName, UniqueName("revoked"), "publish");
                    await Admin.RevokeKeyAsync(key);
                    return key;
                }

            case RefusedPushes.ReadOnlyKey:
                return await Admin.CreateKeyAsync(Fixture.Publisher.UserName, UniqueName("read-only"), "read");
            case RefusedPushes.NoKey:
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "Unknown refused push.");
        }
    }

    public sealed class Setup : ServerFixture
    {
        protected override Task SeedAsync()
            => Api.EnsurePushedAsync(PublishKey, Packages.Base100, Packages.Base110, Packages.Base120Preview, Packages.Dependent100);
    }
}
