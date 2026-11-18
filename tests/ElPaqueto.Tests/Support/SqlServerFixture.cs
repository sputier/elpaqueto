using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace ElPaqueto.Tests;

/// <summary>
/// A test that needs a real SQL Server. It is skipped, with the reason, when no Docker daemon is available and
/// <see cref="DockerAvailability.RequireVariable"/> is not set.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!DockerAvailability.IsRequired && !DockerAvailability.IsAvailable)
        {
            Skip = DockerAvailability.SkipMessage;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}

/// <summary>
/// One SQL Server 2022 server in a container for every SQL Server test of the run; each test gets its own empty database.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    // Pinned to a cumulative update so that a moving tag cannot change the result of a run.
    private const string Image = "mcr.microsoft.com/mssql/server:2022-CU24-ubuntu-22.04";

    private MsSqlContainer _container;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            if (DockerAvailability.IsRequired)
            {
                throw new InvalidOperationException(DockerAvailability.SkipMessage);
            }

            return;
        }

        _container = new MsSqlBuilder(Image).Build();
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Creates an empty database and returns a connection string that points to it.</summary>
    /// <param name="maxPoolSize">The SqlClient connection pool size of the application under test.</param>
    public async Task<string> CreateDatabaseAsync(int maxPoolSize = 20)
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(AdminConnectionString, $"CREATE DATABASE [{name}]");

        return new SqlConnectionStringBuilder(AdminConnectionString)
        {
            InitialCatalog = name,
            MaxPoolSize = maxPoolSize,
        }.ConnectionString;
    }

    /// <summary>A connection that is not pooled, so that a test's own queries never use the application's pool.</summary>
    public async Task<SqlConnection> OpenAsync(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { Pooling = false };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new SqlCommand(sql, connection);
        return (T)Convert.ChangeType(await command.ExecuteScalarAsync(), typeof(T), CultureInfo.InvariantCulture);
    }

    public async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The connection string of the <c>master</c> database, for statements about other databases.</summary>
    public string AdminConnectionString => _container.GetConnectionString();
}
