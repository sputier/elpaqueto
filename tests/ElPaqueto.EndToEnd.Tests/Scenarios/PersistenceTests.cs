using ElPaqueto.EndToEnd.Tests.Harness;

namespace ElPaqueto.EndToEnd.Tests.Scenarios;

/// <summary>
/// #40 scenarios 9 to 11: users, permissions, keys and packages survive a stop and a restart on the same
/// data. On Linux and macOS the stop is SIGTERM with a clean exit; Windows cannot signal a console-less
/// child process without also reaching the test runner, so there the stop is a kill (see docs/technical/e2e-tests.md).
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class PersistenceAfterStopTests : EndToEndTest<PersistenceAfterStopTests.Setup>, IClassFixture<PersistenceAfterStopTests.Setup>
{
    public PersistenceAfterStopTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Scenario09To11_EverythingSurvivesAStopAndARestart()
    {
        await PersistenceScenario.RunAsync(Fixture, Output, abrupt: false, ClientAsync);
    }

    public sealed class Setup : ServerFixture
    {
    }
}

/// <summary>#40 scenario 12: the same after an abrupt stop (the process tree is killed), on every platform.</summary>
[Trait("Category", "EndToEnd")]
public sealed class PersistenceAfterKillTests : EndToEndTest<PersistenceAfterKillTests.Setup>, IClassFixture<PersistenceAfterKillTests.Setup>
{
    public PersistenceAfterKillTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Scenario12_EverythingSurvivesAKillAndARestart()
    {
        await PersistenceScenario.RunAsync(Fixture, Output, abrupt: true, ClientAsync);
    }

    public sealed class Setup : ServerFixture
    {
    }
}

/// <summary>The persistence scenario, shared by the graceful and the abrupt variant.</summary>
internal static class PersistenceScenario
{
    public static async Task RunAsync(
        ServerFixture fixture,
        ITestOutputHelper output,
        bool abrupt,
        Func<UserCredential?, Task<NuGetClient>> createClient)
    {
        var admin = new AdminCli(fixture.Server, output.WriteLine);
        var packages = fixture.Packages;

        // 9. The state to verify: admin, a Read user, a Publish key (fixture), a revoked key, a disabled user,
        //    packages including an unlisted version.
        var revoked = await admin.CreateKeyAsync(fixture.Publisher.UserName, "revoked", "publish");
        await admin.RevokeKeyAsync(revoked);
        var former = await admin.CreateUserAsync("former", "reader");
        await admin.DisableUserAsync(former.UserName);
        var pusher = await createClient(fixture.Reader);
        foreach (var package in new[] { packages.Base100, packages.Base110, packages.Dependent100 })
        {
            (await pusher.PushAsync(package, fixture.PublishKey)).ShouldSucceed();
        }

        await fixture.Api.UnlistAsync(fixture.PublishKey, packages.Base100);
        var users = await admin.ListUsersAsync();
        var keys = await admin.ListKeysAsync();
        var storage = fixture.Api.StorageHashes();
        Assert.NotEmpty(storage);

        // 10. Stop, then restart on the same data, port and configuration.
        StopKind stop;
        if (abrupt)
        {
            await fixture.Server.KillAsync();
            stop = StopKind.Killed;
        }
        else
        {
            stop = await fixture.Server.StopAsync();
        }

        output.WriteLine($"Server stopped: {stop}.");
        Assert.False(fixture.Server.IsRunning);
        await fixture.Server.StartAsync();

        // 11. Nothing was lost, asserted rather than inferred from a successful start.
        var usersAfter = await admin.ListUsersAsync();
        var keysAfter = await admin.ListKeysAsync();
        Assert.True(users == usersAfter, Secrets.Redact($"The users changed.\nBefore: {users}\nAfter: {usersAfter}"));
        Assert.True(keys == keysAfter, Secrets.Redact($"The keys changed.\nBefore: {keys}\nAfter: {keysAfter}"));
        Assert.Equal(storage, fixture.Api.StorageHashes());
        Assert.False(await fixture.Api.IsListedAsync(packages.Base100));
        Assert.True(await fixture.Api.IsListedAsync(packages.Base110));
        Assert.Equal(packages.Base110.Sha512, ServerApi.Sha512((await fixture.Api.DownloadAsync(packages.Base110))!));

        var consumer = await createClient(fixture.Reader);
        var project = consumer.WriteProject("after-restart", [(PackageSet.DependentId, "1.0.0")]);
        await consumer.ClearCachesAsync();
        (await consumer.DotnetAsync(project, ["restore", "--configfile", consumer.ConfigPath])).ShouldSucceed();
        // The lowest version of the range, unlisted or not (see UnlistTests scenario 4).
        Assert.Equal("1.0.0", NuGetClient.ResolvedVersion(project, PackageSet.BaseId));

        (await pusher.PushAsync(packages.Base200, fixture.PublishKey)).ShouldSucceed();
        Assert.True(await fixture.Api.IsListedAsync(packages.Base200));
        (await pusher.PushAsync(packages.Base120Preview, revoked)).ShouldFail().ShouldContain("401 (Unauthorized)");
        Assert.False(await fixture.Api.HasVersionAsync(packages.Base120Preview));

        var disabled = await createClient(former);
        var refused = disabled.WriteProject("disabled", [(PackageSet.BaseId, "1.1.0")]);
        await disabled.ClearCachesAsync();
        (await disabled.DotnetAsync(refused, ["restore", "--configfile", disabled.ConfigPath], TimeSpan.FromMinutes(2)))
            .ShouldFail().ShouldContain("401 (Unauthorized)");
    }
}
