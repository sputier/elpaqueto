using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ElPaqueto.Security.Authorization;

/// <summary>
/// Logs a warning for every endpoint that has no row in the permission table. Such an endpoint is
/// denied, so the warning tells the operator, and the developer who ported a route, why it answers
/// 401 or 403.
/// </summary>
internal sealed partial class EndpointInventoryStartupCheck(
    IServiceProvider services,
    ILogger<EndpointInventoryStartupCheck> logger) : IHostedLifecycleService
{
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        var dataSource = services.GetService<EndpointDataSource>();
        if (dataSource is null)
        {
            return Task.CompletedTask;
        }

        var endpoints = dataSource.Endpoints.ToList();

        foreach (var name in EndpointInventory.FindUnmapped(endpoints))
        {
            LogUnmapped(logger, name);
        }

        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The endpoint '{Endpoint}' has no entry in the permission table and is denied to everyone.")]
    private static partial void LogUnmapped(ILogger logger, string endpoint);
}
