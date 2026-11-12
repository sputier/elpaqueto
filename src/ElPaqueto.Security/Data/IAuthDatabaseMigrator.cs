namespace ElPaqueto.Security.Data;

/// <summary>
/// Applies the migrations of the security database.
/// </summary>
public interface IAuthDatabaseMigrator
{
    /// <summary>
    /// Creates the database when it does not exist and applies every pending migration.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the database is up to date.</returns>
    public Task MigrateAsync(CancellationToken cancellationToken = default);
}
