using System.Net;
using ElPaqueto.Security.Auditing;
using ElPaqueto.Security.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Transport;

/// <summary>
/// Logs the startup warnings of the transport policy: HTTPS not required, and no public base URL
/// on a server that listens beyond loopback.
/// </summary>
internal sealed class TransportStartupWarnings(
    IOptions<ElPaquetoSecurityOptions> options,
    IServiceProvider services,
    ILogger<Audit> logger) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Transport.RequireHttps)
        {
            AuditLog.HttpsNotRequired(logger);
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        // Runs after the server started, when its bound addresses are known.
        if (options.Value.PublicBaseUri is null)
        {
            var addresses = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;

            if (addresses is not null && !AreLoopbackOnly(addresses))
            {
                AuditLog.PublicBaseUrlMissing(logger);
            }
        }

        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Whether every bound address is reachable from this machine only. An address that cannot be
    /// parsed counts as reachable from elsewhere.
    /// </summary>
    internal static bool AreLoopbackOnly(IEnumerable<string> addresses)
    {
        foreach (var address in addresses)
        {
            if (!IsLoopbackOnly(address))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLoopbackOnly(string address)
    {
        // Unix domain sockets are local. BindingAddress rejects some of their forms on Windows.
        if (address.Contains("://unix:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        BindingAddress binding;
        try
        {
            binding = BindingAddress.Parse(address);
        }
        catch (FormatException)
        {
            return false;
        }

        if (binding.IsUnixPipe || binding.IsNamedPipe)
        {
            return true;
        }

        var host = binding.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && TransportSecurityMiddleware.IsLocal(ip);
    }
}
