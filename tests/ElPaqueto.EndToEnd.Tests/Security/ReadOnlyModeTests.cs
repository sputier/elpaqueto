using System.Text.RegularExpressions;
using ElPaqueto.EndToEnd.Tests.Harness;

namespace ElPaqueto.EndToEnd.Tests.Security;

/// <summary>
/// #28: what the real <c>dotnet</c> CLI does with the bare 401 (no <c>WWW-Authenticate</c>) that upstream's write
/// actions return while <c>IsReadOnlyMode</c> is set (ADR 0002, decision 11). The server runs read-only from the
/// start, so nothing is pushed before the tests: the refused writes are asserted from the client's message, the
/// number of requests in the server log and the state of the server afterwards. The facts are recorded in
/// docs/technical/e2e-tests.md.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class ReadOnlyModeTests : EndToEndTest<ReadOnlyModeTests.Setup>, IClassFixture<ReadOnlyModeTests.Setup>
{
    public ReadOnlyModeTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Push_FailsFastWith401_WithoutPromptingOrLooping_AndStoresNothing()
    {
        var package = Fixture.Packages.Base100;
        var client = await ClientAsync(Fixture.Reader);
        var logStart = Fixture.Server.Log.Length;

        var result = await client.PushAsync(package, Fixture.PublishKey);

        var refusals = await AnswersAsync("PUT", "api/v2/package", logStart);
        Output.WriteLine($"Push in read-only mode: {result.Duration.TotalSeconds:0.0} s; PUT api/v2/package answers: {string.Join(", ", refusals)}.");
        result.ShouldFail().ShouldContain("401 (Unauthorized)");
        AssertNoLoop(refusals);
        Assert.False(await Fixture.Api.HasVersionAsync(package), $"{package} is registered after a refused push.");
        Assert.False(File.Exists(Fixture.Api.StoredPackagePath(package)), $"{package} is in the storage after a refused push.");
    }

    [Fact]
    public async Task SymbolPush_FailsFastWith401_WithoutPromptingOrLooping()
    {
        var symbols = Fixture.Packages.Symbols100 with { Path = Path.ChangeExtension(Fixture.Packages.Symbols100.Path, ".snupkg") };
        Assert.True(File.Exists(symbols.Path), $"{symbols.Path} was not packed.");
        var client = await ClientAsync(Fixture.Reader);
        var logStart = Fixture.Server.Log.Length;

        var result = await client.PushAsync(symbols, Fixture.PublishKey);

        var refusals = await AnswersAsync("PUT", "api/v2/symbol", logStart);
        Output.WriteLine($"Symbol push in read-only mode: {result.Duration.TotalSeconds:0.0} s; PUT api/v2/symbol answers: {string.Join(", ", refusals)}.");
        result.ShouldFail().ShouldContain("401 (Unauthorized)");
        AssertNoLoop(refusals);
    }

    [Fact]
    public async Task Delete_FailsFastWith401_WithoutPromptingOrLooping()
    {
        var client = await ClientAsync(Fixture.Reader);
        var logStart = Fixture.Server.Log.Length;

        var result = await client.DeleteAsync(PackageSet.BaseId, "1.0.0", Fixture.PublishKey);

        var refusals = await AnswersAsync("DELETE", "api/v2/package/" + PackageSet.BaseId + "/1.0.0", logStart);
        Output.WriteLine($"Delete in read-only mode: {result.Duration.TotalSeconds:0.0} s; DELETE answers: {string.Join(", ", refusals)}.");
        result.ShouldFail().ShouldContain("401 (Unauthorized)");
        AssertNoLoop(refusals);
    }

    // Each write reached the server, was answered 401 every time, and was not retried in a loop.
    private static void AssertNoLoop(List<string> answers)
    {
        Assert.NotEmpty(answers);
        Assert.All(answers, status => Assert.Equal("401", status));
        Assert.InRange(answers.Count, 1, 6);
    }

    // The log line of a request is written asynchronously and may arrive after the command has exited: wait for it,
    // then a little longer, so that a retry sent right after is counted too.
    private async Task<List<string>> AnswersAsync(string method, string path, int logStart)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Answers(method, path, Fixture.Server.Log[logStart..]).Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
        return Answers(method, path, Fixture.Server.Log[logStart..]);
    }

    // NuGet pushes to the advertised URL with a trailing slash.
    private static List<string> Answers(string method, string path, string log)
        => new Regex($@"Request finished HTTP/1\.1 {method} \S+/{Regex.Escape(path)}/? - (\d{{3}})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)
            .Matches(log)
            .Select(m => m.Groups[1].Value)
            .ToList();

    public sealed class Setup : ServerFixture
    {
        protected override void Configure(IDictionary<string, string?> settings)
            => settings["IsReadOnlyMode"] = "true";
    }
}
