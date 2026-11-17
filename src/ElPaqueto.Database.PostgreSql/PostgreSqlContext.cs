using System;
using System.Threading;
using System.Threading.Tasks;
using ElPaqueto.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ElPaqueto.Database.PostgreSql;

public class PostgreSqlContext : AbstractContext<PostgreSqlContext>
{
    private readonly DatabaseOptions _elpaquetoOptions;

    /// <summary>
    /// The PostgreSql error code for when a unique constraint is violated.
    /// See: https://www.postgresql.org/docs/9.6/errcodes-appendix.html
    /// </summary>
    private const int UniqueConstraintViolationErrorCode = 23505;

    // Serializes the migrations of several servers that start together on one database (a restart racing another
    // start, two replicas): without it both apply the same migration and one fails with "column already exists"
    // (reproduced in the PostgreSQL integration tests before this lock existed). The wait for the lock is bounded by
    // the command timeout (30 s by default) and then fails the startup; a lock held by a crashed server is released
    // with its session, so it cannot stay stuck.
    private const string AcquireMigrationLock = "SELECT pg_advisory_lock(4991760371337265);";
    private const string ReleaseMigrationLock = "SELECT pg_advisory_unlock(4991760371337265);";

    public PostgreSqlContext(DbContextOptions<PostgreSqlContext> efOptions, IOptionsSnapshot<ElPaquetoOptions> elpaquetoOptions)
        : base(efOptions)
    {
        _elpaquetoOptions = elpaquetoOptions.Value.Database;
    }

    public override bool IsUniqueConstraintViolationException(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException postgresException &&
               int.TryParse(postgresException.SqlState, out var code) &&
               code == UniqueConstraintViolationErrorCode;
    }

    public override async Task RunMigrationsAsync(CancellationToken cancellationToken)
    {
        // The advisory lock belongs to the session: keep one connection open for the lock and the migrations.
        await Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await Database.ExecuteSqlRawAsync(AcquireMigrationLock, cancellationToken);
            try
            {
                await base.RunMigrationsAsync(cancellationToken);
            }
            finally
            {
                try
                {
                    await Database.ExecuteSqlRawAsync(ReleaseMigrationLock, CancellationToken.None);
                }
                catch (Exception)
                {
                    // Never mask the exception of the migration: if the unlock fails the session is broken, and
                    // closing it below (or the server crashing) releases the lock anyway.
                }
            }

            // Npgsql caches the database's type information on the initial connection.
            // This causes issues when ElPaqueto creates the database as it may add the citext
            // extension to support case insensitive columns.
            // See: https://github.com/loic-sharma/BaGet/issues/442
            // See: https://github.com/npgsql/efcore.pg/issues/170#issuecomment-303417225
            // The connection is open here, which is what ReloadTypes needs
            // (https://github.com/bagetter/BaGetter/issues/196).
            if (Database.GetDbConnection() is NpgsqlConnection connection)
            {
                connection.ReloadTypes();
            }
        }
        finally
        {
            await Database.CloseConnectionAsync();
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasPostgresExtension("citext");

        builder.Entity<Package>()
            .Property(p => p.Id)
            .HasColumnType("citext");

        builder.Entity<Package>()
            .Property(p => p.NormalizedVersionString)
            .HasColumnType("citext");

        builder.Entity<PackageDependency>()
            .Property(p => p.Id)
            .HasColumnType("citext");

        builder.Entity<PackageType>()
            .Property(p => p.Name)
            .HasColumnType("citext");

        builder.Entity<TargetFramework>()
            .Property(p => p.Moniker)
            .HasColumnType("citext");
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
            optionsBuilder.UseNpgsql(_elpaquetoOptions.ConnectionString);
    }
}
