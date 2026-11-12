using System.Net;
using ElPaqueto.EndToEnd.Tests.Harness;
using ElPaqueto.EndToEnd.Tests.Scenarios;

namespace ElPaqueto.EndToEnd.Tests.Security;

/// <summary>
/// #39: the authorization matrix against the real published server, with the real <c>dotnet</c> CLI where
/// it can express the case and raw HTTP where it cannot. The server runs with
/// <c>PackageDeletionBehavior=HardDelete</c>, so that the operations mapped to <c>Admin</c> exist over HTTP.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class AuthorizationMatrixTests : EndToEndTest<AuthorizationMatrixTests.Setup>, IClassFixture<AuthorizationMatrixTests.Setup>
{
    private static readonly TimeSpan RefusalTimeout = TimeSpan.FromMinutes(2);

    public AuthorizationMatrixTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Row01_NoCredentials_RestoreFailsWith401WithoutPrompting_AndTheIndexChallenges()
    {
        var client = await ClientAsync(sourceCredentials: null);

        var result = await RestoreAsync(client);

        result.ShouldFail().ShouldContain("401 (Unauthorized)");
        using var response = await Fixture.Api.SendAsync(ServerApi.Request(HttpMethod.Get, Fixture.Server.ServiceIndex));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Basic realm=\"ElPaqueto\"", Assert.Single(response.Headers.WwwAuthenticate).ToString());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Row02_WrongPassword_RestoreFailsWith401WithoutPrompting()
    {
        var client = await ClientAsync(new UserCredential(Fixture.Reader.UserName, Secrets.Register("wrong-" + Fixture.Reader.Password)));

        (await RestoreAsync(client)).ShouldFail().ShouldContain("401 (Unauthorized)");
    }

    [Fact]
    public async Task Row03_DisabledUser_RestoreFailsWith401WithoutPrompting()
    {
        var name = UniqueName("disabled");
        var user = await Admin.CreateUserAsync(name, "reader");
        var client = await ClientAsync(user);
        (await RestoreAsync(client)).ShouldSucceed();

        await Admin.DisableUserAsync(name);
        await client.ClearCachesAsync();

        (await RestoreAsync(client)).ShouldFail().ShouldContain("401 (Unauthorized)");
    }

    [Fact]
    public async Task Row04_ReadUser_RestoreSucceeds()
    {
        var client = await ClientAsync(Fixture.Reader);

        (await RestoreAsync(client)).ShouldSucceed();
    }

    [Fact]
    public async Task Row05_UserWithoutRead_IsRefusedWith403_BySearchAndRestore()
    {
        var client = await ClientAsync(Fixture.WithoutRead);

        var search = await client.DotnetAsync(
            client.Root,
            ["package", "search", PackageSet.BaseId, "--source", NuGetClient.SourceName, "--configfile", client.ConfigPath],
            RefusalTimeout);
        var restore = await RestoreAsync(client);

        // dotnet package search reports the refusal without its status code and exits with 0 (see docs/technical/e2e-tests.md).
        Assert.False(search.TimedOut, search.Describe());
        search.ShouldContain("error: Unable to load the service index").ShouldNotContain(PackageSet.BaseId + " |");
        restore.ShouldFail().ShouldContain("403 (Forbidden)");
    }

    [Fact]
    public async Task Row06_PushWithoutAKey_IsRefused_AndThePackageIsAbsent()
    {
        var client = await ClientAsync(Fixture.Reader);

        await RefusedPushes.AssertRefusedAsync(Fixture, Admin, client, RefusedPushes.NoKey, 403, Fixture.Packages.Dependent100);
    }

    [Fact]
    public async Task Row07_PushWithAPublishKey_Succeeds()
    {
        var client = await ClientAsync(Fixture.Reader);

        (await client.PushAsync(Fixture.Packages.Base120Preview, Fixture.PublishKey)).ShouldSucceed();

        Assert.True(await Fixture.Api.HasVersionAsync(Fixture.Packages.Base120Preview));
    }

    [Fact]
    public async Task Row08_PushWithARevokedKey_IsRefused_AndThePackageIsAbsent()
    {
        var client = await ClientAsync(Fixture.Reader);

        await RefusedPushes.AssertRefusedAsync(Fixture, Admin, client, RefusedPushes.RevokedKey, 401, Fixture.Packages.Dependent100);
    }

    [Fact]
    public async Task Row09_NonAdministrator_IsRefusedWith403_OnEveryAdminOperation_WithoutSideEffect()
    {
        var package = Fixture.Packages.Base110;
        var client = await ClientAsync(Fixture.Reader);

        // Hard delete (PackageDeletionBehavior=HardDelete) needs Admin: a Publish key is not enough.
        var delete = await client.DeleteAsync(package.Id, package.Version, Fixture.PublishKey);
        delete.ShouldFail().ShouldContain("403 (Forbidden)");
        using (var rawDelete = await Fixture.Api.SendAsync(ServerApi.Request(HttpMethod.Delete, Fixture.Api.Url($"api/v2/package/{package.Id}/{package.Version}"), apiKey: Fixture.PublishKey)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, rawDelete.StatusCode);
        }

        Assert.True(await Fixture.Api.IsListedAsync(package));
        Assert.True(File.Exists(Fixture.Api.StoredPackagePath(package)));

        // The detailed health report needs Admin.
        var details = Fixture.Api.Url("health/details");
        foreach (var (basic, key) in new (UserCredential?, ApiKey?)[] { (Fixture.Reader, null), (Fixture.Publisher, null), (null, Fixture.PublishKey) })
        {
            using var response = await Fixture.Api.SendAsync(ServerApi.Request(HttpMethod.Get, details, basic, key));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Empty(response.Headers.WwwAuthenticate);
        }

        using var administrator = await Fixture.Api.SendAsync(ServerApi.Request(HttpMethod.Get, details, Fixture.Administrator));
        Assert.Equal(HttpStatusCode.OK, administrator.StatusCode);
    }

    [Fact]
    public async Task Row10_PackageDownloadWithoutRead_ReturnsNoBytes_AndCountsNoDownload()
    {
        var package = Fixture.Packages.Base100;
        var url = Fixture.Api.PackageContentUrl(package.Id, package.Version);
        var downloads = await Fixture.Api.DownloadsAsync(package);
        var wrongPassword = new UserCredential(Fixture.Reader.UserName, Secrets.Register("wrong-" + Fixture.Reader.Password));

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            foreach (var (credential, expected) in new (UserCredential?, HttpStatusCode)[]
            {
                (null, HttpStatusCode.Unauthorized),
                (wrongPassword, HttpStatusCode.Unauthorized),
                (Fixture.WithoutRead, HttpStatusCode.Forbidden),
            })
            {
                using var response = await Fixture.Api.SendAsync(ServerApi.Request(method, url, credential));
                Assert.Equal(expected, response.StatusCode);
                Assert.Empty(await response.Content.ReadAsByteArrayAsync());
                Assert.True((response.Content.Headers.ContentLength ?? 0) == 0, $"{method} {url} announced a body.");
            }
        }

        Assert.Equal(downloads, await Fixture.Api.DownloadsAsync(package));
        // The same URL with Read serves the package: the refusals above are not a broken route.
        Assert.Equal(package.Sha512, ServerApi.Sha512((await Fixture.Api.DownloadAsync(package))!));
    }

    private static async Task<CommandResult> RestoreAsync(NuGetClient client)
    {
        var project = client.WriteProject("restore-" + Guid.NewGuid().ToString("N")[..6], [(PackageSet.BaseId, "1.0.0")]);
        await client.ClearCachesAsync();
        return await client.DotnetAsync(project, ["restore", "--configfile", client.ConfigPath], RefusalTimeout);
    }

    public sealed class Setup : ServerFixture
    {
        /// <summary>A user that authenticates but lacks <c>read</c>.</summary>
        internal UserCredential WithoutRead { get; private set; } = null!;

        protected override void Configure(IDictionary<string, string?> settings)
            => settings["PackageDeletionBehavior"] = "HardDelete";

        protected override async Task SeedAsync()
        {
            WithoutRead = await Admin.CreateUserAsync("no-read", "publish");
            await Api.EnsurePushedAsync(PublishKey, Packages.Base100, Packages.Base110);
        }
    }
}
