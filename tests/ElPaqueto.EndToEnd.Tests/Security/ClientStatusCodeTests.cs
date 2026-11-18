using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using ElPaqueto.EndToEnd.Tests.Harness;

namespace ElPaqueto.EndToEnd.Tests.Security;

/// <summary>
/// What the real <c>dotnet</c> client does with the throttling answers (#39 concerns, #99): how many requests a
/// refused restore sends, that retrying a stale password does not throttle the user, what the client reports for
/// 429 and whether it waits for <c>Retry-After</c>. The server runs with the default throttling settings. The facts
/// are recorded in docs/technical/e2e-tests.md.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed partial class ThrottledClientTests : EndToEndTest<ThrottledClientTests.Setup>, IClassFixture<ThrottledClientTests.Setup>
{
    public ThrottledClientTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task ARestoreWithAStalePassword_FailsWith401_IsCountedOnce_AndTheRightPasswordWorksRightAfter()
    {
        var name = UniqueName("stale");
        var user = await Admin.CreateUserAsync(name, "reader");
        var stale = await ClientAsync(new UserCredential(name, Secrets.Register("stale-" + user.Password)));
        var logStart = Fixture.Server.Log.Length;

        // Two commands with the stale password, as a developer would retry.
        var first = await RestoreAsync(stale);
        var second = await RestoreAsync(stale);

        // NuGet retries a 401 with the same stored credentials (six credentialed attempts per command): the server
        // counts the identical attempt once, so the user is not throttled and the client reports the real cause.
        var log = Fixture.Server.Log[logStart..];
        var failures = PasswordFailures(name).Count(log);
        var statuses = IndexStatuses(log);
        Output.WriteLine($"Two restores with a wrong password: {failures} counted failed sign-in(s); index.json answers: {string.Join(", ", statuses)}.");
        foreach (var refused in new[] { first, second })
        {
            refused.ShouldFail().ShouldContain("NU1301").ShouldContain("401 (Unauthorized)").ShouldNotContain("429");
        }

        Assert.Equal(1, failures);
        Assert.DoesNotContain("429", statuses);
        Assert.True(statuses.Count(s => s == "401") >= 12, $"Expected the client to retry the refused request; index.json answers: {string.Join(", ", statuses)}.");
        Assert.False(log.Contains("Sign-in throttling tripped", StringComparison.Ordinal), "A stale password tripped the throttle.");

        // The right password, from the same address, right after.
        var good = await ClientAsync(user);
        logStart = Fixture.Server.Log.Length;
        var restored = await RestoreAsync(good);
        statuses = IndexStatuses(Fixture.Server.Log[logStart..]);
        Output.WriteLine($"Restore with the right password right after: index.json answers: {string.Join(", ", statuses)}.");
        restored.ShouldSucceed();
        Assert.Contains("200", statuses);
        Assert.DoesNotContain("429", statuses);
    }

    [Fact]
    public async Task DistinctWrongPasswords_StillTripTheDefaultThrottle_AndTheClientThenReports429WithoutWaiting()
    {
        var name = UniqueName("guessed");
        var user = await Admin.CreateUserAsync(name, "reader");
        var index = Fixture.Api.Url("v3/index.json");

        // Five distinct guesses, the default PairFailureLimit, the first four repeated: only the distinct ones count,
        // and the fifth trips the pair.
        for (var i = 0; i < 5; i++)
        {
            var guess = new UserCredential(name, Secrets.Register($"guess-{i}-{user.Password}"));
            for (var repeat = 0; repeat < (i < 4 ? 3 : 1); repeat++)
            {
                using var refused = await Fixture.Api.SendAsync(ServerApi.Request(HttpMethod.Get, index, guess));
                Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            }
        }

        using (var throttledRequest = await Fixture.Api.SendAsync(ServerApi.Request(HttpMethod.Get, index, user)))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, throttledRequest.StatusCode);
            Assert.InRange(throttledRequest.Headers.RetryAfter?.Delta ?? TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
        }

        Assert.True(Fixture.Server.Log.Contains("Sign-in throttling tripped on the Pair counter", StringComparison.Ordinal), "The server did not log a trip of the Pair counter.");

        // The right password is refused while the throttle lasts, and the client does not wait for Retry-After (60 s).
        var good = await ClientAsync(user);
        var logStart = Fixture.Server.Log.Length;
        var stopwatch = Stopwatch.StartNew();
        var throttled = await RestoreAsync(good);
        stopwatch.Stop();

        var statuses = IndexStatuses(Fixture.Server.Log[logStart..]);
        Output.WriteLine($"Restore with the right password while throttled: {stopwatch.Elapsed.TotalSeconds:0.0} s; index.json answers: {string.Join(", ", statuses)}.");
        throttled.ShouldFail().ShouldContain("429 (Too Many Requests)");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(55), "The client waited for the throttle to end.");
        Assert.Contains("429", statuses);
        Assert.DoesNotContain("200", statuses);
    }

    private static async Task<CommandResult> RestoreAsync(NuGetClient client)
    {
        var project = client.WriteProject("restore", [(PackageSet.BaseId, "1.0.0")]);
        await client.ClearCachesAsync();
        return await client.DotnetAsync(project, ["restore", "--configfile", client.ConfigPath], TimeSpan.FromMinutes(2));
    }

    private static List<string> IndexStatuses(string log)
        => IndexAnswer().Matches(log).Select(m => m.Groups[1].Value).ToList();

    // Counted failures only: the repetitions of one already counted are logged at Debug, below the harness's level.
    private static Regex PasswordFailures(string userName)
        => new($@"Authentication with password failed: bad_password \(user {Regex.Escape(userName)}\)", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"Request finished HTTP/1\.1 GET \S+/v3/index\.json - (\d{3})")]
    private static partial Regex IndexAnswer();

    public sealed class Setup : ServerFixture
    {
        protected override void Configure(IDictionary<string, string?> settings)
        {
            // The shipped defaults.
            settings["Security__Throttling__ClientFailureLimit"] = null;
            settings["Security__Throttling__PairFailureLimit"] = null;
            settings["Security__Throttling__AccountFailureLimit"] = null;
        }

        protected override Task SeedAsync()
            => Api.EnsurePushedAsync(PublishKey, Packages.Base100);
    }
}

/// <summary>
/// What the real <c>dotnet</c> client does with <c>503</c> and <c>Retry-After</c> when the server already runs
/// as many password checks as it allows: one verification slot, no queue and no verification cache, held for
/// the whole restore by one sign-in of another user that lasts longer than the restore.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed partial class BusyServerClientTests : EndToEndTest<BusyServerClientTests.Setup>, IClassFixture<BusyServerClientTests.Setup>
{
    public BusyServerClientTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task ARestoreWhileEveryVerificationSlotIsBusy_FailsWith503_AfterAFewQuickRetries()
    {
        var client = await ClientAsync(Fixture.Reader);
        var project = client.WriteProject("busy", [(PackageSet.BaseId, "1.0.0")]);
        await client.ClearCachesAsync();

        // One sign-in, on another URL than the service index so that the log tells the client's requests apart,
        // whose verification holds the only slot for longer than the client's whole restore (see Setup). The
        // restore starts once the slot is known to be held by it, and the sign-in must still be in flight when
        // the restore ends: no request of the client can have found the slot free.
        var blocking = Stopwatch.StartNew();
        var blocker = await HoldTheOnlySlotAsync();
        var logStart = Fixture.Server.Log.Length;
        var stopwatch = Stopwatch.StartNew();
        CommandResult result;
        bool heldThroughout;
        TimeSpan restoreEnded;
        try
        {
            result = await client.DotnetAsync(project, ["restore", "--configfile", client.ConfigPath], TimeSpan.FromMinutes(3));
        }
        finally
        {
            stopwatch.Stop();
            restoreEnded = blocking.Elapsed;
            heldThroughout = !blocker.IsCompleted;

            // Observed in every case, so that a failed restore does not leave the sign-in running unobserved.
            using var blocked = await blocker;
            blocking.Stop();
            Output.WriteLine($"The blocking sign-in held the slot for {blocking.Elapsed.TotalSeconds:0.0} s, {(blocking.Elapsed - restoreEnded).TotalSeconds:0.0} s beyond the end of the restore, and was answered {(int)blocked.StatusCode}.");
            Assert.Equal(HttpStatusCode.OK, blocked.StatusCode);
        }

        var statuses = IndexAnswer().Matches(Fixture.Server.Log[logStart..]).Select(m => m.Groups[1].Value).ToList();
        var busy = statuses.Count(s => s == "503");
        Output.WriteLine($"Restore against a busy server: {stopwatch.Elapsed.TotalSeconds:0.0} s; index.json answers: {string.Join(", ", statuses)} (Retry-After: 5 s).");
        Assert.True(heldThroughout, $"The blocking sign-in ({blocking.Elapsed.TotalSeconds:0.0} s) ended before the restore ({restoreEnded.TotalSeconds:0.0} s after the sign-in was sent): the slot was not held for the whole restore, so the test proves nothing. The sign-in already costs the most the settings accept (Setup): this machine hashes too fast for this test, or the client now retries for longer.");
        result.ShouldFail().ShouldContain("503 (Service Unavailable)");
        Assert.DoesNotContain("200", statuses);
        Assert.True(busy >= 2, $"Expected the client to retry a 503; the server answered 503 {busy} time(s).");
    }

    // Sends the blocking sign-in, then probes the slot with an API key (validated in a slot too, and cheaply) until
    // the probe is refused with 503 while the sign-in is still in flight: nothing else talks to this server, so the
    // sign-in holds the slot from then on. A sign-in that lost the slot to a probe is refused with 503 and sent again.
    private async Task<Task<HttpResponseMessage>> HoldTheOnlySlotAsync()
    {
        var probe = Fixture.PublishKey.AsSourceCredential();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        var blocker = SignIn();
        var lastProbe = "none";
        while (true)
        {
            Assert.True(DateTime.UtcNow < deadline, $"No credential was refused with 503 while the blocking sign-in was in flight (last probe: {lastProbe}).");
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            if (blocker.IsCompleted)
            {
                using var early = await blocker;
                Assert.True(early.StatusCode == HttpStatusCode.ServiceUnavailable, $"The blocking sign-in ended with {(int)early.StatusCode} before the restore started (last probe: {lastProbe}): the server did not refuse the other credentials with 503 while it held the only slot.");
                blocker = SignIn();
                continue;
            }

            using var probed = await Fixture.Api.SendAsync(ServerApi.Request(HttpMethod.Get, Fixture.Api.Url("v3/search?q=probe"), probe));
            lastProbe = ((int)probed.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (probed.StatusCode == HttpStatusCode.ServiceUnavailable && !blocker.IsCompleted)
            {
                return blocker;
            }
        }

        Task<HttpResponseMessage> SignIn()
            => Fixture.Api.SendAsync(ServerApi.Request(HttpMethod.Get, Fixture.Api.Url("v3/search"), Fixture.Blocker));
    }

    [GeneratedRegex(@"Request finished HTTP/1\.1 GET \S+/v3/index\.json - (\d{3})")]
    private static partial Regex IndexAnswer();

    public sealed class Setup : ServerFixture
    {
        /// <summary>The user whose first sign-in holds the only verification slot.</summary>
        internal UserCredential Blocker { get; private set; } = null!;

        protected override void Configure(IDictionary<string, string?> settings)
        {
            settings["Security__Throttling__MaxConcurrentVerifications"] = "1";
            settings["Security__Throttling__VerificationQueueLimit"] = "0";
            settings["Security__Authentication__VerificationCacheTtl"] = "00:00:00";
        }

        // Every user is hashed with the harness's cost (100 000 iterations); the server then restarts with the
        // highest accepted cost. The first sign-in of the blocking user is then the slowest one the settings allow,
        // all of it inside its verification slot: its cheap hash padded to 10 000 000 iterations, then re-hashed
        // with 10 000 000 (about 9 s on a recent desktop, longer on a loaded or slower machine). The client gives
        // up after about 6 s. A hash cannot be interrupted: the slot stays held even if the request is aborted.
        protected override async Task SeedAsync()
        {
            await Api.EnsurePushedAsync(PublishKey, Packages.Base100);
            Blocker = await Admin.CreateUserAsync("blocker", "reader");
            Server.ChangeSettings(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Security__Passwords__IterationCount"] = "10000000",
            });
            await Server.StopAsync();
            await Server.StartAsync();
        }
    }
}
