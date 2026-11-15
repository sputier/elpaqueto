using System.Linq;
using ElPaqueto.Core;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Database.SqlServer;

public class SqlServerContext : AbstractContext<SqlServerContext>
{
    private readonly DatabaseOptions _elpaquetoOptions;

    /// <summary>
    /// The SQL Server error code for when a unique constraint is violated.
    /// </summary>
    private const int UniqueConstraintViolationErrorCode = 2627;

    public SqlServerContext(DbContextOptions<SqlServerContext> efOptions, IOptionsSnapshot<ElPaquetoOptions> elpaquetoOptions)
        : base(efOptions)
    {
        _elpaquetoOptions = elpaquetoOptions.Value.Database;
    }

    /// <summary>
    /// Check whether a <see cref="DbUpdateException"/> is due to a SQL unique constraint violation.
    /// </summary>
    /// <param name="exception">The exception to inspect.</param>
    /// <returns>Whether the exception was caused to SQL unique constraint violation.</returns>
    public override bool IsUniqueConstraintViolationException(DbUpdateException exception)
    {
        if (exception.GetBaseException() is SqlException sqlException)
        {
            return sqlException.Errors
                .OfType<SqlError>()
                .Any(error => error.Number == UniqueConstraintViolationErrorCode);
        }

        return false;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
            optionsBuilder.UseSqlServer(_elpaquetoOptions.ConnectionString);
    }
}
