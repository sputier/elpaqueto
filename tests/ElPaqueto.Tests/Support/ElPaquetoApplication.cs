using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElPaqueto.Core;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Data;
using ElPaqueto.Security.Passwords;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

public class ElPaquetoApplication : WebApplicationFactory<Startup>
{
    private readonly ITestOutputHelper _output;
    private readonly HttpClient _upstreamClient;
    private readonly Action<Dictionary<string, string>> _inMemoryConfiguration;
    private readonly bool _seedTestUser;

    public ElPaquetoApplication(ITestOutputHelper output, HttpClient upstreamClient = null, Action<Dictionary<string, string>> inMemoryConfiguration = null, bool seedTestUser = true)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _upstreamClient = upstreamClient;
        this._inMemoryConfiguration = inMemoryConfiguration;
        _seedTestUser = seedTestUser;
    }

    /// <summary>The user name of the test user seeded in the security database.</summary>
    public string TestUserName { get; } = "test-user";

    /// <summary>A random password, different for every host, never reused anywhere else.</summary>
    public string TestPassword { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    /// <summary>
    /// The token of the test user's API key, which holds every permission: random, different for every factory,
    /// and the same in every host built from it (<c>WithWebHostBuilder</c> builds another host with its own database).
    /// </summary>
    public string TestApiKey => _testKey.Token;

    /// <summary>The public key id of <see cref="TestApiKey"/>.</summary>
    public string TestApiKeyId => _testKey.KeyId;

    private readonly (string KeyId, string Secret, string Token) _testKey = NewTestKey();

    private static (string KeyId, string Secret, string Token) NewTestKey()
    {
        var (keyId, secret) = ApiKeyGenerator.Default.GenerateToken();
        return (keyId, secret, ApiKeyToken.Format(keyId, secret));
    }

    /// <summary>
    /// A client that is NOT authenticated: use it to check what anonymous callers get.
    /// </summary>
    public HttpClient CreateAnonymousClient() => TestUserExtensions.CreateAnonymousClient(this);

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);

        // Clients are authenticated by default, the way `dotnet nuget push` does it: the test user's key, which
        // holds every permission, in X-NuGet-ApiKey. That header alone decides, and writes demand it. Tests of
        // passwords or of keys sent as Basic passwords use CreateClientFor and the other helpers, which send
        // exactly one credential.
        if (_seedTestUser)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(TestUserExtensions.ApiKeyHeader, TestApiKey);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Create temporary storage paths.
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            "ElPaquetoTests",
            Guid.NewGuid().ToString("N"));
        var sqlitePath = Path.Combine(tempPath, "ElPaqueto.db");
        var storagePath = Path.Combine(tempPath, "Packages");

        Directory.CreateDirectory(tempPath);

        builder
            .UseStartup<Startup>()
            .UseEnvironment("Production")
            .ConfigureLogging(logging =>
            {
                // ElPaqueto uses console logging by default. This logger throws operation
                // cancelled exceptions when the host shuts down, causing the the debugger
                // to pause repeatedly if CLR exceptions are enabled.
                logging.ClearProviders();

                // Pipe logs to the xunit output.
                logging.AddProvider(new XunitLoggerProvider(_output));
            })
            .ConfigureAppConfiguration(config =>
            {
                // Setup the integration test configuration.
                var dict = new Dictionary<string, string>
                {
                    { "Database:Type", "Sqlite" },
                    { "Database:ConnectionString", $"Data Source={sqlitePath}" },
                    // Each test host gets its own security database: hosts run in parallel.
                    { "Security:Database:ConnectionString", $"Data Source={Path.Combine(tempPath, "auth.db")}" },
                    { "Storage:Type", "FileSystem" },
                    { "Storage:Path", storagePath },
                    { "Search:Type", "Database" },
                    // The lowest accepted cost keeps the tests fast; each test host has its own security database.
                    { "Security:Passwords:IterationCount", "100000" },
                    { "Mirror:Enabled", _upstreamClient != null ? "true": "false" },
                    { "Mirror:PackageSource", "http://localhost/v3/index.json" },
                };
                _inMemoryConfiguration?.Invoke(dict);

                config.AddInMemoryCollection(dict);

            })
            .ConfigureServices((context, services) =>
            {
                // Make time deterministic for testing purposes.
                var time = new Mock<SystemTime>();
                time
                    .Setup(t => t.UtcNow)
                    .Returns(DateTime.Parse("2020-01-01T00:00:00.000Z"));

                services.AddSingleton(time.Object);
                if (_upstreamClient != null)
                {
                    services.AddSingleton(_upstreamClient);
                }

                services.Configure<HealthCheckServiceOptions>(opts => opts.Registrations.Clear());

                if (_seedTestUser)
                {
                    // Registered after the security module, so it runs after the security database is migrated.
                    services.AddSingleton<IHostedService>(provider => new TestUserSeeder(
                        provider,
                        TestUserName,
                        TestPassword,
                        _testKey.KeyId,
                        _testKey.Secret));
                }

                // Setup the integration test database.
                var provider = services.BuildServiceProvider();
                var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

                // Ensure the database is created before we run migrations. The migrations
                // can create the database too, however, migrations check whether the database exists
                // first. The SQLite provider implements this by attempting to open a connection,
                // and if that fails, creating the database. This throws several exceptions that
                // pauses the debugger repeatedly if CLR exceptions are enabled.
                // See: https://github.com/dotnet/efcore/blob/644d3c8c3a604fd0121d90eaf34f14870e19bcff/src/EFCore.Sqlite.Core/Storage/Internal/SqliteDatabaseCreator.cs#L88-L98
                using var scope = scopeFactory.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<IContext>();
                var dbCreator = ctx.Database.GetService<IRelationalDatabaseCreator>();

                dbCreator.Create();
                ctx.Database.Migrate();
            });
    }
}

/// <summary>
/// Creates the test user, and an API key of that user holding every permission, in the security database
/// before the host starts listening.
/// </summary>
internal sealed class TestUserSeeder(IServiceProvider services, string userName, string password, string keyId, string secret)
    : IHostedLifecycleService
{
    private const ElPaqueto.Security.Permission AllPermissions =
        ElPaqueto.Security.Permission.Read | ElPaqueto.Security.Permission.Publish | ElPaqueto.Security.Permission.Admin;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var passwords = scope.ServiceProvider.GetRequiredService<IPasswordService>();

        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        database.Users.Add(new User
        {
            Id = userId,
            UserName = userName,
            PasswordHash = passwords.Hash(password),
            IsActive = true,
            Permissions = AllPermissions,
            CreatedAt = now,
            UpdatedAt = now,
            PasswordChangedAt = now,
        });

        // Stored the way the API key service stores keys (only the hash of the secret), with a token chosen by the
        // factory so that every host built from it accepts the same key.
        database.ApiKeys.Add(new ApiKey
        {
            Id = Guid.NewGuid(),
            KeyId = keyId,
            SecretHash = ApiKeySecretHash.Compute(secret),
            OwnerUserId = userId,
            Name = "test key",
            Permissions = AllPermissions,
            CreatedAt = now,
        });

        await database.SaveChangesAsync(cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class ElPaquetoWebApplicationFactoryExtensions
{
    public static async Task AddPackageAsync(
        this WebApplicationFactory<Startup> factory,
        Stream package,
        CancellationToken cancellationToken = default)
    {
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();

        using var scope = scopeFactory.CreateScope();
        var indexer = scope.ServiceProvider.GetRequiredService<IPackageIndexingService>();

        var result = await indexer.IndexAsync(package, cancellationToken: cancellationToken);
        if (result != PackageIndexingResult.Success)
        {
            throw new InvalidOperationException($"Unexpected indexing result {result}");
        }
    }

    public static async Task AddSymbolPackageAsync(
        this WebApplicationFactory<Startup> factory,
        Stream symbolPackage,
        CancellationToken cancellationToken = default)
    {
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();

        using var scope = scopeFactory.CreateScope();
        var indexer = scope.ServiceProvider.GetRequiredService<ISymbolIndexingService>();

        var result = await indexer.IndexAsync(symbolPackage, cancellationToken);
        if (result != SymbolIndexingResult.Success)
        {
            throw new InvalidOperationException($"Unexpected indexing result {result}");
        }
    }
}
