using System.Net;
using System.Text.Json;
using ElPaqueto.EndToEnd.Tests.Harness;

namespace ElPaqueto.EndToEnd.Tests.Security;

/// <summary>
/// #39 crawl: every URL the server advertises, for a stable and a prerelease package, is same-origin, is
/// served with <c>Read</c>, is refused with 401 without credentials or with wrong ones and with 403 without
/// <c>Read</c>, for GET and HEAD, and never returns package bytes when refused.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class ServiceIndexCrawlTests : EndToEndTest<ServiceIndexCrawlTests.Setup>, IClassFixture<ServiceIndexCrawlTests.Setup>
{
    // 1 index + 6 distinct resource URLs + per package (2): registration index (shared), flat versions (shared),
    // .nupkg, .nuspec, readme, registration leaf, icon; plus search and autocomplete queries.
    private const int KnownMinimum = 20;

    public ServiceIndexCrawlTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task TheCrawl_ReachesTheKnownMinimum_AndCoversEveryIndexResource()
    {
        var urls = await Fixture.CrawlAsync();
        foreach (var url in urls.OrderBy(u => u.Url.AbsoluteUri, StringComparer.Ordinal))
        {
            Output.WriteLine($"{url.Kind,-6} {url.Url}  <- {url.FoundIn}");
        }

        Output.WriteLine($"{urls.Count} URLs crawled.");
        Assert.True(urls.Count >= KnownMinimum, $"Only {urls.Count} URLs were crawled; at least {KnownMinimum} are known to exist.");
        var resources = urls.Where(u => u.FoundIn.StartsWith("index.json", StringComparison.Ordinal) && !u.FoundIn.Contains('(', StringComparison.Ordinal))
            .Select(u => u.Url.AbsoluteUri)
            .ToHashSet();
        Assert.Equal(6, resources.Count);
        foreach (var path in new[] { "api/v2/package", "api/v2/symbol", "v3/search", "v3/registration/", "v3/package/", "v3/autocomplete" })
        {
            Assert.Contains(new Uri(Fixture.Server.BaseAddress, path).AbsoluteUri, resources);
        }

        // URLs the server put in its responses, not only those derived by the protocol.
        Assert.Contains(urls, u => u.Url.AbsolutePath.EndsWith("/1.2.0-preview.1.json", StringComparison.Ordinal));
        Assert.Contains(urls, u => u.Url.AbsolutePath.EndsWith("/icon", StringComparison.Ordinal) && u.FoundIn.StartsWith("a response", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryCrawledUrl_HasTheOriginOfTheServer()
    {
        var urls = await Fixture.CrawlAsync();

        var foreign = urls.Where(u => !ServiceIndexCrawler.SameOrigin(u.Url, Fixture.Server.BaseAddress)).ToList();

        Assert.True(foreign.Count == 0, "URLs of another origin: " + string.Join(", ", foreign.Select(u => $"{u.Url} ({u.FoundIn})")));
    }

    [Fact]
    public async Task EveryCrawledUrl_AnswersARequestWithReadCredentials()
    {
        var failures = new List<string>();
        foreach (var url in await Fixture.CrawlAsync())
        {
            var method = url.Kind == CrawledKind.Write ? HttpMethod.Put : HttpMethod.Get;
            using var response = await Fixture.Api.SendAsync(ServerApi.Request(method, url.Url, Fixture.Reader));
            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsByteArrayAsync();
            Output.WriteLine($"{method} {url.Url}: {status}, {body.Length} bytes");

            // Read resources are served; prefixes are not resources (no route: refused like any unknown URL);
            // writes need a Publish key in X-NuGet-ApiKey, which Basic credentials never are.
            var expected = url.Kind == CrawledKind.Read ? 200 : 403;
            if (status != expected || (expected == 200 && body.Length == 0))
            {
                failures.Add($"{method} {url.Url} ({url.FoundIn}): {status}, {body.Length} bytes, expected {expected}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task EveryCrawledUrl_Is401WithoutCredentialsOrWithWrongOnes_ForEveryMethod_AndReturnsNothing()
    {
        var wrong = new UserCredential(Fixture.Reader.UserName, Secrets.Register("wrong-" + Fixture.Reader.Password));
        var failures = new List<string>();
        var replays = 0;
        foreach (var url in await Fixture.CrawlAsync())
        {
            foreach (var method in MethodsOf(url))
            {
                foreach (var credential in new[] { null, wrong })
                {
                    replays++;
                    using var response = await Fixture.Api.SendAsync(ServerApi.Request(method, url.Url, credential));
                    var body = await response.Content.ReadAsByteArrayAsync();
                    var challenge = response.Headers.WwwAuthenticate.ToString();
                    if (response.StatusCode != HttpStatusCode.Unauthorized || body.Length != 0 || challenge != "Basic realm=\"ElPaqueto\"")
                    {
                        failures.Add($"{method} {url.Url} {(credential is null ? "without credentials" : "with a wrong password")}: {(int)response.StatusCode}, {body.Length} bytes, challenge '{challenge}'");
                    }
                }
            }
        }

        Output.WriteLine($"{replays} anonymous and wrong-password replays.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task EveryCrawledUrl_Is403ForAUserWithoutRead_ForEveryMethod_AndReturnsNothing()
    {
        var failures = new List<string>();
        foreach (var url in await Fixture.CrawlAsync())
        {
            foreach (var method in MethodsOf(url))
            {
                using var response = await Fixture.Api.SendAsync(ServerApi.Request(method, url.Url, Fixture.WithoutRead));
                var body = await response.Content.ReadAsByteArrayAsync();
                if (response.StatusCode != HttpStatusCode.Forbidden || body.Length != 0 || response.Headers.WwwAuthenticate.Count != 0)
                {
                    failures.Add($"{method} {url.Url}: {(int)response.StatusCode}, {body.Length} bytes");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task ForwardedHeadersFromAnUntrustedClient_NeverReachTheGeneratedUrls()
    {
        // No trusted proxy covers 127.0.0.1 in this fixture: the test client is an untrusted one.
        var failures = new List<string>();
        foreach (var url in (await Fixture.CrawlAsync()).Where(u => u.Kind == CrawledKind.Read && IsJson(u.Url)))
        {
            using var request = ServerApi.Request(HttpMethod.Get, url.Url, Fixture.Reader);
            request.Headers.Add("X-Forwarded-Host", "attacker.example.com");
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-For", "203.0.113.7");
            using var response = await Fixture.Api.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            if (body.Contains("attacker.example.com", StringComparison.OrdinalIgnoreCase) || body.Contains("https://", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{url.Url}: {body}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        using var index = await Fixture.Api.SendAsync(ServerApi.Request(HttpMethod.Get, Fixture.Server.ServiceIndex, Fixture.Reader));
        using var json = JsonDocument.Parse(await index.Content.ReadAsStringAsync());
        Assert.All(
            json.RootElement.GetProperty("resources").EnumerateArray(),
            r => Assert.StartsWith(Fixture.Server.BaseAddress.AbsoluteUri, r.GetProperty("@id").GetString(), StringComparison.Ordinal));
    }

    private static HttpMethod[] MethodsOf(CrawledUrl url)
        => url.Kind == CrawledKind.Write ? [HttpMethod.Put, HttpMethod.Get] : [HttpMethod.Get, HttpMethod.Head];

    private static bool IsJson(Uri url)
        => url.AbsolutePath.EndsWith(".json", StringComparison.Ordinal) || url.Query.Length > 0 || url.AbsolutePath.EndsWith("/search", StringComparison.Ordinal) || url.AbsolutePath.EndsWith("/autocomplete", StringComparison.Ordinal);

    public sealed class Setup : ServerFixture
    {
        private Task<IReadOnlyList<CrawledUrl>>? _crawl;

        /// <summary>A user that authenticates but lacks <c>read</c>.</summary>
        internal UserCredential WithoutRead { get; private set; } = null!;

        /// <summary>The crawl, run once for the class.</summary>
        internal Task<IReadOnlyList<CrawledUrl>> CrawlAsync()
            => _crawl ??= ServiceIndexCrawler.CrawlAsync(Api, Server.ServiceIndex, [Packages.Base100, Packages.Base120Preview]);

        protected override void Configure(IDictionary<string, string?> settings)
        {
            // Trust only a documentation address: forwarded headers from 127.0.0.1 are then untrusted.
            settings["Security__ReverseProxy__KnownProxies__0"] = "192.0.2.10";
        }

        protected override async Task SeedAsync()
        {
            WithoutRead = await Admin.CreateUserAsync("no-read", "publish,admin");
            await Api.EnsurePushedAsync(PublishKey, Packages.Base100, Packages.Base120Preview);
        }
    }
}
