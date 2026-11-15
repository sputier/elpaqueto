#nullable enable

using System;
using System.Linq;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.GenericAttributes;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ElPaqueto.Web;

/// <summary>
/// The Markdown pipeline for package READMEs and release notes. Their content comes from anyone
/// who can publish a package, or from a mirrored upstream package, and is shown to every user of
/// the web UI: it must never be able to run script in the server's origin.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Raw HTML is disabled: tags are rendered as text.</item>
/// <item>The generic attributes extension (<c>{onclick=...}</c>, <c>{x-init=...}</c>) is removed:
/// it lets Markdown put any attribute on an element, including event handlers and the Alpine.js
/// directives that the page evaluates.</item>
/// <item>Link and image URLs are allow-listed: a link keeps its URL only when it is relative or
/// uses <c>http</c>, <c>https</c> or <c>mailto</c>; an image only when it is relative or uses
/// <c>http</c> or <c>https</c>. Any other URL (<c>javascript:</c>, <c>data:</c>,
/// <c>vbscript:</c>, ...) is replaced by an empty one, and such an autolink is rendered as text.</item>
/// </list>
/// </remarks>
public static partial class PackageMarkdown
{
    private static readonly string[] LinkSchemes = ["http", "https", "mailto"];
    private static readonly string[] ImageSchemes = ["http", "https"];

    /// <summary>Creates the pipeline. The result is immutable and can be shared.</summary>
    public static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml();

        if (!builder.Extensions.TryRemove<GenericAttributesExtension>())
        {
            // A Markdig upgrade that renames or moves the extension must not silently re-enable it.
            throw new InvalidOperationException("The generic attributes extension could not be removed from the Markdown pipeline.");
        }
        builder.DocumentProcessed += NeutralizeUnsafeUrls;

        return builder.Build();
    }

    /// <summary>
    /// Whether a URL may be rendered. The scheme is read the way browsers read it: leading and
    /// trailing control characters and spaces are ignored, and tabs and line breaks are ignored
    /// anywhere, so <c>" java\tscript:"</c> is a <c>javascript:</c> URL.
    /// </summary>
    public static bool IsSafeUrl(string? url, bool isImage)
    {
        if (string.IsNullOrEmpty(url))
        {
            return true;
        }

        var normalized = new string(url.Where(c => c is not ('\t' or '\n' or '\r')).ToArray())
            .Trim(Enumerable.Range(0, 0x21).Select(i => (char)i).ToArray());

        var match = SchemePattern().Match(normalized);
        if (!match.Success)
        {
            // No scheme: a relative URL, a fragment or a scheme-relative URL (//host/path).
            return true;
        }

        var scheme = match.Groups[1].Value;
        return (isImage ? ImageSchemes : LinkSchemes).Contains(scheme, StringComparer.OrdinalIgnoreCase);
    }

    private static void NeutralizeUnsafeUrls(MarkdownDocument document)
    {
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (!IsSafeUrl(link.Url, link.IsImage))
            {
                link.Url = string.Empty;
            }

            // A URL computed at render time (heading references) is checked when it is computed.
            var dynamicUrl = link.GetDynamicUrl;
            if (dynamicUrl is not null)
            {
                var isImage = link.IsImage;
                link.GetDynamicUrl = () =>
                {
                    var url = dynamicUrl();
                    return IsSafeUrl(url, isImage) ? url : string.Empty;
                };
            }
        }

        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
        {
            // An e-mail autolink is rendered with mailto: and cannot carry another scheme.
            if (!autolink.IsEmail && !IsSafeUrl(autolink.Url, isImage: false))
            {
                autolink.ReplaceBy(new LiteralInline(autolink.Url));
            }
        }
    }

    [GeneratedRegex("^([a-zA-Z][a-zA-Z0-9+.-]*):")]
    private static partial Regex SchemePattern();
}
