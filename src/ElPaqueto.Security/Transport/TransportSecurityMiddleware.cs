using System.Net;
using ElPaqueto.Security.Auditing;
using ElPaqueto.Security.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Transport;

/// <summary>
/// Applies the transport policy right after trusted forwarded headers are processed: rejects
/// remote plain-HTTP requests, then pins the request's scheme and host to the public base URL.
/// </summary>
/// <remarks>
/// The check runs before the public base URL is applied, otherwise every request would look like
/// HTTPS. It runs before authentication and before any endpoint, so credentials sent over plain
/// HTTP are never verified and the server never answers a 401 challenge that would invite a
/// client to send credentials over plain HTTP. It never redirects: a redirect makes clients that
/// already sent credentials send them again.
/// </remarks>
internal sealed class TransportSecurityMiddleware
{
    /// <summary>The body of the 403 answer to a remote plain-HTTP request.</summary>
    internal const string HttpsRequiredMessage = "HTTPS is required.";

    private const string ForwardedProtoHeader = "X-Forwarded-Proto";
    private const string ForwardedForHeader = "X-Forwarded-For";

    private readonly RequestDelegate _next;
    private readonly bool _requireHttps;
    private readonly string? _publicScheme;
    private readonly HostString _publicHost;
    private readonly ILogger<Audit> _logger;
    private int _untrustedForwarderReported;

    public TransportSecurityMiddleware(
        RequestDelegate next,
        IOptions<ElPaquetoSecurityOptions> options,
        ILogger<Audit> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
        _requireHttps = options.Value.Transport.RequireHttps;

        var publicBaseUri = options.Value.PublicBaseUri;
        if (publicBaseUri is not null)
        {
            _publicScheme = publicBaseUri.Scheme;
            // The ASCII (punycode) host, and no port when it is the default: https://host/, not https://host:443/.
            _publicHost = publicBaseUri.IsDefaultPort
                ? new HostString(publicBaseUri.IdnHost)
                : new HostString(publicBaseUri.IdnHost, publicBaseUri.Port);
        }
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_requireHttps && !context.Request.IsHttps && !IsLocal(context.Connection.RemoteIpAddress))
        {
            ReportUntrustedForwarder(context);

            // One line per refused request: the address is the client's after trusted forwarded-header processing.
            AuditLog.TransportRejected(
                _logger, AuditValues.Scheme(context.Request.Scheme), AuditValues.Client(context.Connection.RemoteIpAddress));

            var response = context.Response;
            response.StatusCode = StatusCodes.Status403Forbidden;
            response.ContentType = "text/plain; charset=utf-8";
            response.Headers.CacheControl = "no-store";
            return response.WriteAsync(HttpsRequiredMessage, context.RequestAborted);
        }

        if (_publicScheme is not null)
        {
            context.Request.Scheme = _publicScheme;
            context.Request.Host = _publicHost;
        }

        return _next(context);
    }

    /// <summary>
    /// Whether a client address is on this machine. <see langword="null"/> means a transport
    /// without IP addresses (Unix socket, named pipe, in-process test server), which is local.
    /// </summary>
    internal static bool IsLocal(IPAddress? address)
    {
        if (address is null)
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address);
    }

    private void ReportUntrustedForwarder(HttpContext context)
    {
        // Applied forwarded headers are removed by the forwarded-headers middleware: one that is
        // still present was not applied, usually because its sender is not a trusted proxy.
        // Reported once, as a hint for a misconfigured proxy, not for each request anyone can send.
        var headers = context.Request.Headers;
        if (!headers.ContainsKey(ForwardedProtoHeader) && !headers.ContainsKey(ForwardedForHeader))
        {
            return;
        }

        if (Interlocked.Exchange(ref _untrustedForwarderReported, 1) == 0)
        {
            AuditLog.UntrustedForwardedHeaders(_logger, AuditValues.Client(context.Connection.RemoteIpAddress));
        }
    }
}
