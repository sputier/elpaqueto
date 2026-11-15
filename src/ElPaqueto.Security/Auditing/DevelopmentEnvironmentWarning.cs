using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ElPaqueto.Security.Auditing;

/// <summary>
/// Logs a startup warning when the server runs in the <c>Development</c> environment, where the developer
/// exception page shows request details, including the <c>Authorization</c> header, to whoever causes an error.
/// </summary>
internal sealed class DevelopmentEnvironmentWarning(IHostEnvironment environment, ILogger<Audit> audit) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment())
        {
            AuditLog.DevelopmentEnvironment(audit);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
