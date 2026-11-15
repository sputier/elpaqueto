using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// The web UI carries the ElPaqueto branding (#53): title, favicon, header logo, and no link to the
/// upstream project except the explicit BaGetter credit.
/// </summary>
public class WebUiBrandingIntegrationTests
{
    private const string Credit = "https://github.com/bagetter/BaGetter";

    private static readonly Regex Hrefs = new("href=\"([^\"]*)\"", RegexOptions.IgnoreCase);

    private readonly ITestOutputHelper _output;

    public WebUiBrandingIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("/", "Packages - ElPaqueto")]
    [InlineData("/upload", "Upload package - ElPaqueto")]
    [InlineData("/stats", "Stats - ElPaqueto")]
    [InlineData("/packages/Not.A.Package", "Not.A.Package - ElPaqueto")]
    public async Task EveryPage_ShowsTheElPaquetoTitleFaviconAndLogo(string path, string title)
    {
        using var app = new ElPaquetoApplication(_output);
        using var client = app.CreateClient();

        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"<title>{title}</title>", html, StringComparison.Ordinal);
        Assert.Contains("""<link rel="icon" href="/_content/ElPaqueto.Web/favicon.ico" """, html, StringComparison.Ordinal);
        Assert.Contains("""href="/_content/ElPaqueto.Web/images/elpaqueto-icon-32.png" """, html, StringComparison.Ordinal);
        Assert.Contains("""<link rel="apple-touch-icon" href="/_content/ElPaqueto.Web/images/elpaqueto-icon-180.png">""", html, StringComparison.Ordinal);

        // The header logo has an alt attribute and a fixed size, so that it causes no layout shift.
        var logo = Regex.Match(html, """<img src="/_content/ElPaqueto.Web/images/elpaqueto-header-48.png"[^>]*>""");
        Assert.True(logo.Success, "The header logo was not found.");
        Assert.Contains("""width="40" height="40" alt="" """, Regex.Replace(logo.Value, @"\s+", " "), StringComparison.Ordinal);
        Assert.Contains("<span>ElPaqueto</span>", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/upload")]
    [InlineData("/stats")]
    [InlineData("/packages/Not.A.Package")]
    public async Task NoPage_LinksToTheUpstreamProject_ExceptTheCredit(string path)
    {
        // "/" and the unknown package render the "nothing here" and "not found" help links.
        using var app = new ElPaquetoApplication(_output);
        using var client = app.CreateClient();

        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("bagetter.com", html, StringComparison.OrdinalIgnoreCase);
        var upstreamLinks = Hrefs.Matches(html)
            .Select(match => match.Groups[1].Value)
            .Where(href => href.Contains("bagetter", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal([Credit], upstreamLinks);
        Assert.Contains($"""Based on <a href="{Credit}" """, html, StringComparison.Ordinal);
        Assert.Contains("https://github.com/sputier/ElPaqueto/tree/develop/docs/user", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("favicon.ico")]
    [InlineData("images/elpaqueto-icon-32.png")]
    [InlineData("images/elpaqueto-icon-180.png")]
    [InlineData("images/elpaqueto-header-48.png")]
    [InlineData("images/elpaqueto-header-96.png")]
    public void BrandingImages_ExistWithinTheSizeBudget(string path)
    {
        // The test host runs in Production without the static web assets manifest, so the images are
        // checked on disk; serving them is checked against the published server (see the pull request).
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ElPaqueto.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var file = new FileInfo(Path.Combine(directory.FullName, "src", "ElPaqueto.Web", "wwwroot", path));
        Assert.True(file.Exists, $"{path} is missing.");
        Assert.InRange(file.Length, 100, 200 * 1024);
    }
}
