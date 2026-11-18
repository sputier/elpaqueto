using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ElPaqueto.Tests;

/// <summary>
/// Whether a Docker daemon answers, which the container tests (PostgreSQL, S3) need. The result is computed once per run.
/// </summary>
internal static class DockerAvailability
{
    /// <summary>
    /// When set to a non-empty value, a missing Docker daemon fails the container tests instead of skipping them.
    /// The CI job sets it so that the tests can never be skipped silently there.
    /// </summary>
    public const string RequireVariable = "ELPAQUETO_REQUIRE_DOCKER";

    private static readonly Lazy<string> Problem = new(Probe);

    public static bool IsAvailable => Problem.Value is null;

    public static bool IsRequired => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RequireVariable));

    public static string SkipMessage =>
        $"Docker is not available ({Problem.Value}): the container integration tests (PostgreSQL, S3) need a Docker daemon (Testcontainers). "
        + $"Start Docker, or set {RequireVariable}=1 to make this a failure.";

    private static string Probe()
    {
        try
        {
            var info = new ProcessStartInfo("docker", ["info", "--format", "{{.ServerVersion}} {{.OSType}}"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill(entireProcessTree: true);
                return "`docker info` did not answer within 30 seconds";
            }

            return process.ExitCode == 0 && output.Result.Trim().EndsWith(" linux", StringComparison.Ordinal)
                ? null
                : $"the Docker daemon does not run Linux containers (`docker info`: {output.Result.Trim()} {error.Result.Trim()})";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "the docker command was not found";
        }
    }
}

/// <summary>
/// A test that needs a real PostgreSQL. It is skipped, with the reason, when no Docker daemon is available and
/// <see cref="DockerAvailability.RequireVariable"/> is not set.
/// </summary>
public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (!DockerAvailability.IsRequired && !DockerAvailability.IsAvailable)
        {
            Skip = DockerAvailability.SkipMessage;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSql";
}

/// <summary>
/// One PostgreSQL server in a container for every PostgreSQL test of the run; each test gets its own empty database.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    // The major version the owner deploys with; the minor version is whatever the tag points to.
    private const string Image = "postgres:17.12-alpine";

    private PostgreSqlContainer _container;

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

        _container = new PostgreSqlBuilder(Image).Build();
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
    /// <param name="maxPoolSize">The Npgsql connection pool size of the application under test.</param>
    public async Task<string> CreateDatabaseAsync(int maxPoolSize = 20)
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = name,
            MaxPoolSize = maxPoolSize,
        }.ConnectionString;
    }

    /// <summary>A connection that is not pooled, so that a test's own queries never use the application's pool.</summary>
    public async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)Convert.ChangeType(await command.ExecuteScalarAsync(), typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The admin connection string (database <c>postgres</c>), for statements about other databases.</summary>
    public string AdminConnectionString => _container.GetConnectionString();
}
