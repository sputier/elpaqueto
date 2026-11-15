using ElPaqueto.Security.Configuration;
using ElPaqueto.Security.Headers;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Tests;

public class SecurityHeadersMiddlewareTests
{
    private const string Hsts = "max-age=31536000";

    [Theory]
    [InlineData("text/html", true)]
    [InlineData("text/html; charset=utf-8", true)]
    [InlineData("TEXT/HTML", true)]
    [InlineData("application/xhtml+xml", true)]
    [InlineData("application/json", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData("image/svg+xml", false)]
    [InlineData("text/plain", false)]
    [InlineData("text/htmlx", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHtml_RecognizesHtmlDocumentsOnly(string? contentType, bool expected)
    {
        Assert.Equal(expected, SecurityHeadersMiddleware.IsHtml(contentType));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("app.localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.1.2.3", true)]
    [InlineData("[::1]", true)]
    [InlineData("nuget.example.test", false)]
    [InlineData("localhost.example.test", false)]
    [InlineData("192.0.2.10", false)]
    [InlineData("", false)]
    public void IsLoopbackHost_RecognizesThisMachine(string host, bool expected)
    {
        Assert.Equal(expected, SecurityHeadersMiddleware.IsLoopbackHost(new HostString(host)));
    }

    [Fact]
    public void AnHtmlResponse_GetsThePagePolicyAndNoStore()
    {
        var context = Context(StatusCodes.Status200OK, "text/html; charset=utf-8");

        SecurityHeadersMiddleware.Apply(context, strictTransportSecurity: null);

        var headers = context.Response.Headers;
        Assert.Equal(SecurityHeadersMiddleware.HtmlContentSecurityPolicy, headers.ContentSecurityPolicy.ToString());
        Assert.Equal("no-store", headers.CacheControl.ToString());
        Assert.Equal("DENY", headers.XFrameOptions.ToString());
        Assert.Equal("nosniff", headers.XContentTypeOptions.ToString());
        Assert.Equal("no-referrer", headers["Referrer-Policy"].ToString());
    }

    [Fact]
    public void ASuccessfulResource_KeepsItsCaching()
    {
        var context = Context(StatusCodes.Status200OK, "application/octet-stream");
        context.Response.Headers.CacheControl = "max-age=3600";

        SecurityHeadersMiddleware.Apply(context, strictTransportSecurity: null);

        var headers = context.Response.Headers;
        Assert.Equal(SecurityHeadersMiddleware.ResourceContentSecurityPolicy, headers.ContentSecurityPolicy.ToString());
        Assert.Equal("max-age=3600", headers.CacheControl.ToString());
        Assert.Equal("nosniff", headers.XContentTypeOptions.ToString());
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status403Forbidden)]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status429TooManyRequests)]
    [InlineData(StatusCodes.Status500InternalServerError)]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    public void AnErrorResponse_IsNeverStored(int status)
    {
        var context = Context(status, contentType: null);
        context.Response.Headers.CacheControl = "public, max-age=3600";

        SecurityHeadersMiddleware.Apply(context, strictTransportSecurity: null);

        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void ANotModifiedResponse_DoesNotReplaceTheStoredPolicy()
    {
        var context = Context(StatusCodes.Status304NotModified, contentType: null);

        SecurityHeadersMiddleware.Apply(context, strictTransportSecurity: null);

        Assert.False(context.Response.Headers.ContainsKey("Content-Security-Policy"));
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions.ToString());
    }

    [Theory]
    [InlineData("https", "nuget.example.test", true)]
    [InlineData("http", "nuget.example.test", false)]
    [InlineData("https", "localhost", false)]
    [InlineData("https", "127.0.0.1", false)]
    public void Hsts_IsSentOverHttpsToARealHostOnly(string scheme, string host, bool expected)
    {
        var context = Context(StatusCodes.Status200OK, "application/json");
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);

        SecurityHeadersMiddleware.Apply(context, Hsts);

        Assert.Equal(expected ? Hsts : string.Empty, context.Response.Headers.StrictTransportSecurity.ToString());
    }

    [Fact]
    public void Hsts_IsNeverSentWhenDisabled()
    {
        var context = Context(StatusCodes.Status200OK, "application/json");
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("nuget.example.test");

        SecurityHeadersMiddleware.Apply(context, strictTransportSecurity: null);

        Assert.False(context.Response.Headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public void ThePagePolicy_KeepsTheProtectionsItDocuments()
    {
        var directives = SecurityHeadersMiddleware.HtmlContentSecurityPolicy
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToDictionary(d => d.Split(' ')[0], d => d);

        Assert.Equal("default-src 'self'", directives["default-src"]);
        Assert.Equal("object-src 'none'", directives["object-src"]);
        Assert.Equal("base-uri 'self'", directives["base-uri"]);
        Assert.Equal("form-action 'self'", directives["form-action"]);
        Assert.Equal("frame-ancestors 'none'", directives["frame-ancestors"]);
        Assert.Equal("connect-src 'self'", directives["connect-src"]);
        Assert.Equal("style-src 'self'", directives["style-src"]);
        Assert.DoesNotContain("https:", directives["script-src"], StringComparison.Ordinal);
        Assert.DoesNotContain("*", SecurityHeadersMiddleware.HtmlContentSecurityPolicy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://ui.example.test")]
    public void TheCorsPolicy_NeverAllowsCredentialsOrEveryOrigin(string? origin)
    {
        string[] origins = origin is null ? [] : [origin];
        var options = new ElPaquetoSecurityOptions();
        options.Cors.AllowedOrigins = origins;
        var cors = new CorsOptions();

        new ConfigureElPaquetoCors(Options.Create(options)).Configure(cors);

        var policy = cors.GetPolicy(ConfigureElPaquetoCors.PolicyName);
        Assert.NotNull(policy);
        Assert.False(policy.SupportsCredentials);
        Assert.False(policy.AllowAnyOrigin);
        Assert.False(policy.AllowAnyHeader);
        Assert.False(policy.AllowAnyMethod);
        Assert.Equal(origins, policy.Origins);
    }

    private static DefaultHttpContext Context(int status, string? contentType)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("nuget.example.test");
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        return context;
    }
}
