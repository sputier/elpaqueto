using ElPaqueto.Security.Headers;
using ElPaqueto.Security.Transport;
using Microsoft.AspNetCore.Builder;

namespace ElPaqueto.Security;

/// <summary>
/// Middleware of the ElPaqueto security module.
/// </summary>
public static class ElPaquetoSecurityPipelineExtensions
{
    /// <summary>
    /// Processes forwarded headers from trusted proxies only, rejects remote plain-HTTP requests
    /// when HTTPS is required, and applies the public base URL.
    /// </summary>
    /// <param name="app">The application pipeline.</param>
    /// <returns>The same <paramref name="app"/> instance, to allow chaining.</returns>
    /// <remarks>
    /// Replaces <c>UseForwardedHeaders</c>. Must be the first middleware that reads the request's
    /// scheme, host or client address, before <c>UsePathBase</c> and authentication. Requires
    /// <c>AddElPaquetoSecurity</c>.
    /// </remarks>
    public static IApplicationBuilder UseElPaquetoForwardedHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseForwardedHeaders();
        app.UseMiddleware<TransportSecurityMiddleware>();

        return app;
    }

    /// <summary>
    /// Adds the security response headers: <c>X-Content-Type-Options</c>, <c>Referrer-Policy</c>,
    /// <c>X-Frame-Options</c> and <c>Content-Security-Policy</c> on every response,
    /// <c>Cache-Control: no-store</c> on HTML pages and error responses, and
    /// <c>Strict-Transport-Security</c> over HTTPS when <c>Security:Hsts:Enabled</c> is set.
    /// </summary>
    /// <param name="app">The application pipeline.</param>
    /// <returns>The same <paramref name="app"/> instance, to allow chaining.</returns>
    /// <remarks>
    /// Register it before every middleware that can answer a request, including
    /// <see cref="UseElPaquetoForwardedHeaders"/>: the headers are set when the response starts, from
    /// the effective scheme left by the forwarded headers. Requires <c>AddElPaquetoSecurity</c>.
    /// </remarks>
    public static IApplicationBuilder UseElPaquetoSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }

    /// <summary>
    /// Applies the CORS policy built from <c>Security:Cors</c>.
    /// </summary>
    /// <param name="app">The application pipeline.</param>
    /// <returns>The same <paramref name="app"/> instance, to allow chaining.</returns>
    /// <remarks>
    /// Must run after <c>UseRouting</c> and before <c>UseAuthorization</c>: a preflight request
    /// carries no credentials, so it must be answered before authorization would refuse it.
    /// Requires <c>AddElPaquetoSecurity</c>.
    /// </remarks>
    public static IApplicationBuilder UseElPaquetoCors(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseCors(ConfigureElPaquetoCors.PolicyName);
    }
}
