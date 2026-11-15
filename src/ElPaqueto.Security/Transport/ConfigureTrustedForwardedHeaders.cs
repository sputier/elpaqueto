using System.Net;
using ElPaqueto.Security.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace ElPaqueto.Security.Transport;

/// <summary>
/// Replaces whatever earlier configuration set on <see cref="ForwardedHeadersOptions"/> (the
/// inherited server trusts every client) with the trusted proxies of <c>Security:ReverseProxy</c>.
/// </summary>
/// <remarks>
/// A post-configuration runs after every <c>Configure</c>, so the inherited configuration cannot
/// widen the trust again.
/// </remarks>
internal sealed class ConfigureTrustedForwardedHeaders(IOptions<ElPaquetoSecurityOptions> security)
    : IPostConfigureOptions<ForwardedHeadersOptions>
{
    public void PostConfigure(string? name, ForwardedHeadersOptions options)
    {
        var settings = security.Value;
        var proxy = settings.ReverseProxy;

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // With a public base URL, the host is fixed: a forwarded host is never needed.
        if (settings.PublicBaseUri is null)
        {
            options.ForwardedHeaders |= ForwardedHeaders.XForwardedHost;
        }

        options.ForwardLimit = proxy.ForwardLimit;
        options.AllowedHosts.Clear();

        // The obsolete KnownNetworks collection is a view of KnownIPNetworks: clearing the latter
        // also drops what earlier configuration added through the former.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        if (!proxy.HasExplicitProxies)
        {
            options.KnownIPNetworks.Add(new IPNetwork(IPAddress.Loopback, 8));
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
            return;
        }

        foreach (var value in proxy.KnownProxies)
        {
            if (SecurityReverseProxyOptions.TryParseProxy(value, out var address))
            {
                options.KnownProxies.Add(address);
            }
        }

        foreach (var value in proxy.KnownNetworks)
        {
            if (SecurityReverseProxyOptions.TryParseNetwork(value, out var network)
                && !SecurityReverseProxyOptions.IsCatchAll(network))
            {
                options.KnownIPNetworks.Add(network);
            }
        }
    }
}
