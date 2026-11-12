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
}
