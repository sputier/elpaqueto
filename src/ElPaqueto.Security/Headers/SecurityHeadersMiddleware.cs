using System.Globalization;
using System.Net;
using ElPaqueto.Security.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace ElPaqueto.Security.Headers;

/// <summary>
/// Adds the security response headers to every response, right before it starts, so that they are
/// also present on responses produced by later middleware (static files, the transport policy,
/// authorization challenges, error pages).
/// </summary>
/// <remarks>
/// The headers are decided when the response starts, from its final status and content type and
/// from the request's effective scheme, which is known by then: trusted forwarded headers and the
/// public base URL have been applied.
/// </remarks>
internal sealed class SecurityHeadersMiddleware
{
    /// <summary>
    /// The policy of HTML pages. It fits the current web UI, which needs <c>'unsafe-eval'</c>
    /// (Alpine.js 2 compiles its directives with <c>new Function</c>), <c>'unsafe-inline'</c> scripts
    /// (inline script blocks and event handler attributes), package icons from any https host, and
    /// the icon font of Office UI Fabric, which its stylesheet loads from Microsoft's CDN.
    /// </summary>
    internal const string HtmlContentSecurityPolicy =
        "default-src 'self'; "
        + "script-src 'self' 'unsafe-inline' 'unsafe-eval'; "
        + "style-src 'self'; "
        + "img-src 'self' data: https:; "
        + "font-src 'self' " + FabricFontOrigins + "; "
        + "connect-src 'self'; "
        + "object-src 'none'; "
        + "base-uri 'self'; "
        + "form-action 'self'; "
        + "frame-ancestors 'none'";

    /// <summary>
    /// The policy of every other response (JSON, packages, icons, readme files, static files). It
    /// only matters when such a response is opened as a document, for example a package icon in SVG
    /// format opened directly: nothing it contains may run, load or be framed.
    /// </summary>
    internal const string ResourceContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; sandbox";

    /// <summary>
    /// The origins of the icon font referenced by <c>fabric.min.css</c>: the URL in the stylesheet
    /// and the CDN it redirects to. A Content Security Policy also checks the target of a redirect.
    /// </summary>
    internal const string FabricFontOrigins = "https://static2.sharepointonline.com https://res-1.cdn.office.net";

    internal const string NoStore = "no-store";
    internal const string NoReferrer = "no-referrer";
    internal const string ReferrerPolicyHeader = "Referrer-Policy";

    private static readonly Func<object, Task> OnStartingCallback = OnStarting;

    private readonly RequestDelegate _next;
    private readonly string? _strictTransportSecurity;

    public SecurityHeadersMiddleware(RequestDelegate next, IOptions<ElPaquetoSecurityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);

        _next = next;

        var hsts = options.Value.Hsts;
        if (hsts.Enabled)
        {
            _strictTransportSecurity = "max-age=" + hsts.MaxAgeSeconds.ToString(CultureInfo.InvariantCulture)
                + (hsts.IncludeSubDomains ? "; includeSubDomains" : string.Empty);
        }
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(OnStartingCallback, new State(context, _strictTransportSecurity));
        return _next(context);
    }

    private static Task OnStarting(object state)
    {
        var (context, strictTransportSecurity) = (State)state;
        Apply(context, strictTransportSecurity);
        return Task.CompletedTask;
    }

    /// <summary>Sets the headers of a response that is about to start.</summary>
    internal static void Apply(HttpContext context, string? strictTransportSecurity)
    {
        var request = context.Request;
        var response = context.Response;
        var headers = response.Headers;
        var isHtml = IsHtml(response.ContentType);

        headers.XContentTypeOptions = "nosniff";
        headers[ReferrerPolicyHeader] = NoReferrer;
        headers.XFrameOptions = "DENY";
        // A 304 has no content type, and browsers merge its headers into the cached response: it must
        // not replace the policy stored with a cached page.
        if (response.StatusCode != StatusCodes.Status304NotModified)
        {
            headers.ContentSecurityPolicy = isHtml ? HtmlContentSecurityPolicy : ResourceContentSecurityPolicy;
        }

        // Pages and errors only: package, registration and symbol responses keep their own caching.
        if (isHtml || response.StatusCode >= StatusCodes.Status400BadRequest)
        {
            headers.CacheControl = NoStore;
        }

        if (strictTransportSecurity is not null && request.IsHttps && !IsLoopbackHost(request.Host))
        {
            headers.StrictTransportSecurity = strictTransportSecurity;
        }
    }

    /// <summary>Whether a content type is an HTML document.</summary>
    internal static bool IsHtml(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType) || !MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
        {
            return false;
        }

        return mediaType.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || mediaType.MediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the host is this machine. Browsers would remember an HSTS policy for <c>localhost</c>
    /// and apply it to every local development server, so it is never sent there.
    /// </summary>
    internal static bool IsLoopbackHost(HostString host)
    {
        var name = host.Host;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(name.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }

    private sealed record State(HttpContext Context, string? StrictTransportSecurity);
}
