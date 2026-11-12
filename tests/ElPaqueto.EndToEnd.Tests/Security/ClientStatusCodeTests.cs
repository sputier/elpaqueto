using System.Diagnostics;
using System.Text.RegularExpressions;
using ElPaqueto.EndToEnd.Tests.Harness;

namespace ElPaqueto.EndToEnd.Tests.Security;

/// <summary>
/// What the real <c>dotnet</c> client does with the throttling answers (#39 concerns): how many requests a
/// refused restore sends, what it reports for 429 and whether it waits for <c>Retry-After</c>. The server runs
/// with the default throttling settings. The facts are recorded in docs/technical/e2e-tests.md.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed partial class ThrottledClientTests : EndToEndTest<ThrottledClientTests.Setup>, IClassFixture<ThrottledClientTests.Setup>
{
    public ThrottledClientTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task ARestoreWithAStalePassword_TripsTheDefaultThrottleByItself_ThenTheRightPasswordGets429()
    {
        var name = UniqueName("stale");
        var user = await Admin.CreateUserAsync(name, "reader");
        var stale = await ClientAsync(new UserCredential(name, Secrets.Register("stale-" + user.Password)));
        var logStart = Fixture.Server.Log.Length;

        var refused = await RestoreAsync(stale);

        // NuGet retries a 401 with the same stored credentials: one restore sends more failed sign-ins than
        // the default PairFailureLimit (5), so the user is throttled by a single command, and the last answer
        // the client reports is 429, not 401.
        var failures = PasswordFailures(name).Count(Fixture.Server.Log[logStart..]);
        var statuses = IndexStatuses(Fixture.Server.Log[logStart..]);
        Output.WriteLine($"One restore with a wrong password: {failures} failed sign-ins; index.json answers: {string.Join(", ", statuses)}.");
        refused.ShouldFail().ShouldContain("429 (Too Many Requests)");
        Assert.Equal(5, failures);
        Assert.True(Fixture.Server.Log[logStart..].Contains("Sign-in throttling tripped on the Pair counter", StringComparison.Ordinal), "The server did not log a trip of the Pair counter.");

        // The right password is refused too while the throttle lasts, and the client does not wait for Retry-After (60 s).
        var good = await ClientAsync(user);
        logStart = Fixture.Server.Log.Length;
        var stopwatch = Stopwatch.StartNew();
        var throttled = await RestoreAsync(good);
        stopwatch.Stop();

        statuses = IndexStatuses(Fixture.Server.Log[logStart..]);
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
/// as many password checks as it allows: one verification slot, no queue, no verification cache, and a
/// slow hash kept busy by another user for the whole restore.
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

        using var stop = new CancellationTokenSource();
        // Three loops of slow sign-ins by another user, on another URL than the service index so that the log
        // tells the client's requests apart: whenever one loop's verification ends, another one is waiting to
        // take the only slot, so the slot is never free for the client.
        var blockers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var response = await Fixture.Api.SendAsync(ServerApi.Request(HttpMethod.Get, Fixture.Api.Url("v3/search"), Fixture.Administrator));
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5));
                }
            }
        })).ToArray();
        int logStart;
        CommandResult result;
        var stopwatch = new Stopwatch();
        try
        {
            await WaitForAVerificationAsync();
            logStart = Fixture.Server.Log.Length;
            stopwatch.Start();
            result = await client.DotnetAsync(project, ["restore", "--configfile", client.ConfigPath], TimeSpan.FromMinutes(3));
            stopwatch.Stop();
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(blockers);
        }

        var statuses = IndexAnswer().Matches(Fixture.Server.Log[logStart..]).Select(m => m.Groups[1].Value).ToList();
        var busy = statuses.Count(s => s == "503");
        Output.WriteLine($"Restore against a busy server: {stopwatch.Elapsed.TotalSeconds:0.0} s; index.json answers: {string.Join(", ", statuses)} (Retry-After: 5 s).");
        result.ShouldFail().ShouldContain("503 (Service Unavailable)");
        Assert.True(busy >= 2, $"Expected the client to retry a 503; the server answered 503 {busy} time(s).");
    }

    // Until a blocker's sign-in has been answered once, then a little more: the slot is busy from then on.
    private async Task WaitForAVerificationAsync()
    {
        var start = Fixture.Server.Log.Length;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!Fixture.Server.Log[start..].Contains("/v3/search - 200", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(300));
    }
    [GeneratedRegex(@"Request finished HTTP/1\.1 GET \S+/v3/index\.json - (\d{3})")]
    private static partial Regex IndexAnswer();

    public sealed class Setup : ServerFixture
    {
        protected override void Configure(IDictionary<string, string?> settings)
        {
            settings["Security__Throttling__MaxConcurrentVerifications"] = "1";
            settings["Security__Throttling__VerificationQueueLimit"] = "0";
            settings["Security__Authentication__VerificationCacheTtl"] = "00:00:00";
            // A slow hash (about a second or two) keeps the single slot busy.
            settings["Security__Passwords__IterationCount"] = "2000000";
        }

        protected override Task SeedAsync()
            => Api.EnsurePushedAsync(PublishKey, Packages.Base100);
    }
}
