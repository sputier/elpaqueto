using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ElPaqueto.Security;
using NuGet.Packaging;
using NuGet.Versioning;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// The web UI (#27): no side effect before authorization, no configured services disclosed by
/// default, and package metadata that cannot run script in the page.
/// </summary>
public class WebUiIntegrationTests
{
    private const string Password = "a long enough passphrase for tests";

    private const string Payload = """
        <script>alert('script-tag')</script>

        <img src=x onerror="alert('img-onerror')">

        <iframe src="https://evil.example.com/frame"></iframe>

        <div onmouseover="alert('handler')">hover</div>

        [javascript link](javascript:alert('js-link'))

        [entity link](&#106;avascript:alert('entity-link'))

        [data link](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)

        <javascript:alert('autolink')>

        [attributes](https://example.com){onclick="alert('generic-attribute')" x-init="alert('alpine')"}

        ![image](javascript:alert('js-image'))

        [safe link](https://example.com/docs) and **bold** text.
        """;

    /// <summary>
    /// An attribute of a real tag (event handler, Alpine directive or shorthand) whose value runs the
    /// payload. The page's own handlers never call alert, and escaped text is not inside a tag.
    /// </summary>
    private static readonly Regex AttributeRunningThePayload = new(
        """<[^>]*\s(on\w+|x-[\w:.-]+|@[\w:.-]+)\s*=[^>]*alert""",
        RegexOptions.IgnoreCase);

    private readonly ITestOutputHelper _output;

    public WebUiIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task AnonymousPackagePage_WithMirrorEnabled_MakesNoUpstreamRequest()
    {
        using var upstream = new ElPaquetoApplication(_output);
        await upstream.AddPackageAsync(TestResources.GetResourceStream(TestResources.Package));
        var counter = new CountingHandler { InnerHandler = upstream.Server.CreateHandler() };
        using var upstreamClient = new HttpClient(counter) { BaseAddress = upstream.Server.BaseAddress };
        upstreamClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{upstream.TestUserName}:{upstream.TestPassword}")));
        using var downstream = new ElPaquetoApplication(_output, upstreamClient);
        await downstream.AddUserAsync("publisher", Password, Permission.Publish);

        using var anonymous = downstream.CreateAnonymousClient();
        using var publisher = downstream.CreateClientFor("publisher", Password);
        using var anonymousResponse = await anonymous.GetAsync("packages/TestData");
        using var forbiddenResponse = await publisher.GetAsync("packages/TestData/1.2.3");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);
        Assert.Equal(0, counter.Count);

        // Control: the same page, authorized, does reach the upstream source.
        using var reader = downstream.CreateClient();
        using var allowedResponse = await reader.GetAsync("packages/TestData");

        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
        Assert.True(counter.Count > 0, "The authorized request should have queried the upstream source.");
    }

    [Fact]
    public async Task SearchQuery_CannotBreakOutOfThePrereleaseHandler()
    {
        // Index.cshtml puts a search link inside an inline onclick handler, in a JavaScript string.
        using var app = new ElPaquetoApplication(_output);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/?q=" + Uri.EscapeDataString("x';alert(1);'"));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var handler = Regex.Match(html, """onclick="(window\.location\.href=[^"]*)""");
        Assert.True(handler.Success, "The prerelease handler was not found.");
        // The quote of the query is percent-encoded: the JavaScript string is never closed early.
        Assert.Matches("""^window\.location\.href='[^']*'$""", WebUtility.HtmlDecode(handler.Groups[1].Value));
        Assert.Contains("%27", handler.Groups[1].Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatisticsPage_DoesNotListTheConfiguredServices_WithTheShippedSettings()
    {
        using var app = new ElPaquetoApplication(_output);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("stats");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Total amount of packages", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Services in use", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Sqlite", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FileSystem", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StatisticsPage_ListsTheConfiguredServices_OnlyWhenAskedTo()
    {
        // Control for the test above: the same page with the setting on does show them.
        using var app = new ElPaquetoApplication(_output, inMemoryConfiguration: dict => dict["Statistics:ListConfiguredServices"] = "true");
        using var client = app.CreateClient();

        using var response = await client.GetAsync("stats");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Services in use", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackagePage_RendersReadmeAndReleaseNotes_WithoutAnythingExecutable()
    {
        using var app = new ElPaquetoApplication(_output);
        await app.AddPackageAsync(BuildPackage("Xss.Package", readme: Payload, releaseNotes: Payload));
        using var client = app.CreateClient();

        using var response = await client.GetAsync("packages/Xss.Package");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // No tag, event handler, javascript:/data: URL or Alpine directive of the payload survives.
        Assert.DoesNotContain("<script>alert", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img src=x", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<div onmouseover", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(AttributeRunningThePayload, html);
        Assert.DoesNotContain("href=\"javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"data:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"javascript:", html, StringComparison.OrdinalIgnoreCase);

        // Rendered, as text, in both the README and the release notes.
        Assert.Equal(2, CountOccurrences(html, "&lt;script&gt;alert("));
        Assert.Equal(2, CountOccurrences(html, "<a href=\"https://example.com/docs\">safe link</a>"));
        Assert.Equal(2, CountOccurrences(html, "<strong>bold</strong>"));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static MemoryStream BuildPackage(string id, string readme, string releaseNotes)
    {
        var builder = new PackageBuilder
        {
            Id = id,
            Version = NuGetVersion.Parse("1.0.0"),
            Description = "A package whose metadata carries script.",
            ReleaseNotes = releaseNotes,
            Readme = "README.md",
        };
        builder.Authors.Add("Test author");
        builder.Files.Add(new InMemoryPackageFile("README.md", Encoding.UTF8.GetBytes(readme)));

        var stream = new MemoryStream();
        builder.Save(stream);
        stream.Position = 0;
        return stream;
    }

    private sealed class InMemoryPackageFile(string path, byte[] content) : IPackageFile
    {
        public string Path => path;

        public string EffectivePath => path;

        public NuGet.Frameworks.NuGetFramework NuGetFramework => null;

        public System.Runtime.Versioning.FrameworkName TargetFramework => null;

        public DateTimeOffset LastWriteTime => DateTimeOffset.UnixEpoch.AddYears(30);

        public Stream GetStream() => new MemoryStream(content, writable: false);
    }

    private sealed class CountingHandler : DelegatingHandler
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
