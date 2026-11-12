using System;
using Markdig;
using Xunit;

namespace ElPaqueto.Web.Tests;

public class PackageMarkdownFacts
{
    private static readonly MarkdownPipeline Pipeline = PackageMarkdown.CreatePipeline();

    private static string Render(string markdown) => Markdown.ToHtml(markdown, Pipeline);

    [Theory]
    [InlineData("<script>alert(1)</script>", "&lt;script&gt;alert(1)&lt;/script&gt;")]
    [InlineData("before <script>alert(1)</script> after", "before &lt;script&gt;alert(1)&lt;/script&gt; after")]
    [InlineData("<img src=x onerror=alert(1)>", "&lt;img src=x onerror=alert(1)&gt;")]
    [InlineData("<iframe src=\"https://evil.example.com\"></iframe>", "&lt;iframe src=&quot;https://evil.example.com&quot;&gt;&lt;/iframe&gt;")]
    [InlineData("<div x-data x-init=\"alert(1)\">x</div>", "&lt;div x-data x-init=&quot;alert(1)&quot;&gt;x&lt;/div&gt;")]
    public void RawHtml_IsRenderedAsText(string markdown, string expected)
    {
        var html = Render(markdown);

        Assert.Contains(expected, html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<div", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](JavaScript:alert(1))")]
    [InlineData("[x](<java\tscript:alert(1)>)")]
    [InlineData("[x](&#106;avascript:alert(1))")]
    [InlineData("[x](javascript&colon;alert(1))")]
    [InlineData("[x](vbscript:msgbox(1))")]
    [InlineData("[x](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)")]
    [InlineData("[x][ref]\n\n[ref]: javascript:alert(1)")]
    [InlineData("[x]\n\n[x]: javascript:alert(1)")]
    public void AnUnsafeLink_LosesItsUrl(string markdown)
    {
        var html = Render(markdown);

        Assert.Contains("<a href=\"\">x</a>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("script:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("![x](javascript:alert(1))")]
    [InlineData("![x](data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=)")]
    [InlineData("![x](mailto:someone@example.com)")]
    public void AnUnsafeImage_LosesItsUrl(string markdown)
    {
        var html = Render(markdown);

        Assert.Contains("<img src=\"\" alt=\"x\" />", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("<vbscript:msgbox(1)>")]
    [InlineData("<data:text/html,hello>")]
    public void AnUnsafeAutolink_IsRenderedAsText(string markdown)
    {
        var html = Render(markdown);

        Assert.DoesNotContain("<a", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[x](https://example.com){onclick=\"alert(1)\"}")]
    [InlineData("# Title {onmouseover=alert(1)}")]
    [InlineData("[x](https://example.com){x-init=\"alert(1)\"}")]
    [InlineData("[x](https://example.com){@click=\"alert(1)\"}")]
    [InlineData("::: {onclick=alert(1) x-init=alert(2)}\ntext\n:::")]
    [InlineData("| a |\n|---|\n| b {onclick=alert(1)} |")]
    [InlineData("```js {onclick=alert(1)}\ncode\n```")]
    public void GenericAttributes_AreNotApplied(string markdown)
    {
        var html = Render(markdown);

        Assert.DoesNotMatch("<[^>]*\\s(on\\w+|x-[\\w:.-]+|@[\\w:.-]+)\\s*=", html);
    }

    [Theory]
    [InlineData("[x](https://example.com/a)", "<a href=\"https://example.com/a\">x</a>")]
    [InlineData("[x](http://example.com/a)", "<a href=\"http://example.com/a\">x</a>")]
    [InlineData("[x](mailto:someone@example.com)", "<a href=\"mailto:someone@example.com\">x</a>")]
    [InlineData("[x](docs/usage.md)", "<a href=\"docs/usage.md\">x</a>")]
    [InlineData("[x](#usage)", "<a href=\"#usage\">x</a>")]
    [InlineData("[x](//example.com/a)", "<a href=\"//example.com/a\">x</a>")]
    [InlineData("![logo](https://example.com/logo.png)", "<img src=\"https://example.com/logo.png\" alt=\"logo\" />")]
    [InlineData("<https://example.com/a>", "<a href=\"https://example.com/a\">https://example.com/a</a>")]
    [InlineData("<someone@example.com>", "<a href=\"mailto:someone@example.com\">someone@example.com</a>")]
    [InlineData("see https://example.com/a", "<a href=\"https://example.com/a\">https://example.com/a</a>")]
    [InlineData("**bold** and `code`", "<strong>bold</strong> and <code>code</code>")]
    [InlineData("| a | b |\n|---|---|\n| 1 | 2 |", "<td>1</td>")]
    [InlineData("- [x] done", "checked=\"checked\"")]
    public void SafeMarkdown_StillRenders(string markdown, string expected)
    {
        Assert.Contains(expected, Render(markdown), StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceToAHeading_KeepsItsAnchor()
    {
        var html = Render("# Usage\n\nSee [Usage].");

        Assert.Contains("<a href=\"#usage\">Usage</a>", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("relative/path", true)]
    [InlineData("/absolute/path", true)]
    [InlineData("?query", true)]
    [InlineData("#fragment", true)]
    [InlineData("a/b:c", true)]
    [InlineData("HTTPS://example.com", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(" \u0001javascript:alert(1)", false)]
    [InlineData("java\nscript:alert(1)", false)]
    [InlineData("java\rscript:alert(1)", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("file:///etc/passwd", false)]
    public void IsSafeUrl_ReadsTheSchemeLikeABrowser(string url, bool expected)
    {
        Assert.Equal(expected, PackageMarkdown.IsSafeUrl(url, isImage: false));
    }
}
