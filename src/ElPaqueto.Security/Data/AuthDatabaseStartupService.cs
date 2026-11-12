using Microsoft.Extensions.Hosting;

namespace ElPaqueto.Security.Data;

/// <summary>
/// Migrates the security database before the server accepts requests. If the database cannot be
/// migrated, the exception stops the host: the server must never run without its schema.
/// </summary>
internal sealed class AuthDatabaseStartupService(IAuthDatabaseMigrator migrator) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => migrator.MigrateAsync(cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
