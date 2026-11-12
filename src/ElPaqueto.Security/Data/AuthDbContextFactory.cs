using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ElPaqueto.Security.Data;

/// <summary>
/// Lets <c>dotnet ef</c> create the context without building the full host:
/// <c>dotnet ef migrations add &lt;Name&gt; --project src/ElPaqueto.Security --output-dir Data/Migrations</c>.
/// </summary>
internal sealed class AuthDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    // Design-time only: the file is never created because no command connects to it.
    private const string DesignTimeConnectionString = "Data Source=:memory:";

    /// <inheritdoc />
    public AuthDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlite(
                DesignTimeConnectionString,
                sqlite => sqlite.MigrationsHistoryTable(AuthDbContext.MigrationsHistoryTable))
            .Options;

        return new AuthDbContext(options);
    }
}
