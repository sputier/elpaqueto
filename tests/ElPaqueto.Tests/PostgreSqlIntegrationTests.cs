using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ElPaqueto.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// The real host (the same pipeline as every other integration test) with <c>Database:Type=PostgreSql</c>, against a
/// real PostgreSQL 17 started by Testcontainers. Each test works in its own empty database of one shared container.
/// The tests are skipped, with the reason, when no Docker daemon is available; the CI job sets
/// <c>ELPAQUETO_REQUIRE_DOCKER=1</c> so that they cannot be skipped there. See docs/technical/testing.md.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "PostgreSql")]
public sealed class PostgreSqlIntegrationTests(PostgreSqlFixture postgres, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly List<ElPaquetoApplication> _apps = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.DisposeAsync();
        }
    }

    private ElPaquetoApplication Start(string connectionString, Action<Dictionary<string, string>> configure = null)
    {
        var app = new ElPaquetoApplication(
            output,
            inMemoryConfiguration: dict =>
            {
                dict["Database:Type"] = "PostgreSql";
                dict["Database:ConnectionString"] = connectionString;
                configure?.Invoke(dict);
            },
            keepHealthChecks: true);
        _apps.Add(app);
        return app;
    }

    private static async Task<HttpResponseMessage> PushAsync(HttpClient client, byte[] package)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(package), "package", "package.nupkg");
        return await client.PutAsync("api/v2/package", content);
    }

    private static async Task<JsonNode> GetJsonAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url} answered {(int)response.StatusCode}.");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync());
    }

    private static string[] Versions(JsonNode flatContainerIndex)
        => flatContainerIndex["versions"].AsArray().Select(v => v.GetValue<string>()).ToArray();

    private async Task<long> RowsAsync(string connectionString, string table)
        => await postgres.ScalarAsync<long>(connectionString, $"SELECT count(*) FROM \"{table}\"");

    // Migrations.

    [PostgreSqlFact]
    public async Task Migrations_OnAnEmptyDatabase_CreateTheWholeSchema()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        Assert.Equal(0, await postgres.ScalarAsync<long>(connectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'"));
        var app = Start(connectionString);

        using var client = app.CreateClient();
        using var response = await client.GetAsync("v3/index.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IContext>();
        var known = context.Database.GetMigrations().Count();
        Assert.True(known >= 9, "The PostgreSQL provider should carry its nine historical migrations at least.");
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(known, await RowsAsync(connectionString, "__EFMigrationsHistory"));
        foreach (var table in new[] { "Packages", "PackageDependencies", "PackageTypes", "TargetFrameworks" })
        {
            Assert.Equal(1, await postgres.ScalarAsync<long>(
                connectionString,
                $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{table}'"));
        }

        Assert.Equal(1, await postgres.ScalarAsync<long>(connectionString, "SELECT count(*) FROM pg_extension WHERE extname = 'citext'"));
    }

    [PostgreSqlFact]
    public async Task CaseInsensitiveColumns_AreCitext()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        using var client = Start(connectionString).CreateClient();
        using var _ = await client.GetAsync("v3/index.json");

        var citextColumns = await postgres.ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND udt_name = 'citext' "
            + "AND (table_name, column_name) IN (('Packages', 'Id'), ('Packages', 'Version'), ('PackageDependencies', 'Id'), ('PackageTypes', 'Name'), ('TargetFrameworks', 'Moniker'))");

        Assert.Equal(5, citextColumns);
    }

    [PostgreSqlFact]
    public async Task Migrations_OnAnAlreadyMigratedDatabase_AreIdempotentEvenWithAnOpenConnection()
    {
        // https://github.com/bagetter/BaGetter/issues/196: "connection already open" when the migration entry point
        // runs on a connection that is open, and ReloadTypes needs one.
        var connectionString = await postgres.CreateDatabaseAsync();
        var app = Start(connectionString);
        using var client = app.CreateClient();
        using var _ = await client.GetAsync("v3/index.json");
        var before = await RowsAsync(connectionString, "__EFMigrationsHistory");

        for (var i = 0; i < 3; i++)
        {
            using var scope = app.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IContext>();
            await context.Database.OpenConnectionAsync();
            await context.RunMigrationsAsync(default);
            await context.RunMigrationsAsync(default);
        }

        Assert.Equal(before, await RowsAsync(connectionString, "__EFMigrationsHistory"));
    }

    [PostgreSqlFact]
    public async Task TwoServersStartingTogetherOnAnEmptyDatabase_BothComeUp()
    {
        // Two containers started by one `docker compose up`, or a restart racing a health-check-driven restart.
        var connectionString = await postgres.CreateDatabaseAsync();
        var first = Start(connectionString);
        var second = Start(connectionString);

        var statuses = await Task.WhenAll(
            Task.Run(async () => { using var c = first.CreateClient(); using var r = await c.GetAsync("v3/index.json"); return r.StatusCode; }),
            Task.Run(async () => { using var c = second.CreateClient(); using var r = await c.GetAsync("v3/index.json"); return r.StatusCode; }));

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));
        using var scope = first.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IContext>();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    [PostgreSqlFact]
    public async Task ADatabaseWhoseVersionColumnIsCaseSensitive_IsRepairedAtStartup()
    {
        // https://github.com/bagetter/BaGetter/issues/239: databases created by BaGet keep Packages.Version as
        // varchar(64) although FixVersionCaseSensitivity is recorded as applied. Rebuild that state, then start.
        var connectionString = await postgres.CreateDatabaseAsync();
        await using (var first = Start(connectionString))
        {
            using var client = first.CreateClient();
            using var _ = await client.GetAsync("v3/index.json");
        }

        await postgres.ExecuteAsync(connectionString, "ALTER TABLE \"Packages\" ALTER COLUMN \"Version\" TYPE character varying(64)");
        await postgres.ExecuteAsync(connectionString, "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%EnsureVersionColumnIsCaseInsensitive'");
        Assert.Equal("character varying", await postgres.ScalarAsync<string>(
            connectionString,
            "SELECT data_type FROM information_schema.columns WHERE table_name = 'Packages' AND column_name = 'Version'"));

        var app = Start(connectionString, dict => dict["PackageDeletionBehavior"] = "HardDelete");
        using var client2 = app.CreateClient();
        using var pushed = await PushAsync(client2, TestPackageBuilder.Create("Legacy.Pkg", "5.7.22-Hdbfd3d9a85-b6"));
        Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);

        Assert.Equal("citext", await postgres.ScalarAsync<string>(
            connectionString,
            "SELECT udt_name FROM information_schema.columns WHERE table_name = 'Packages' AND column_name = 'Version'"));

        // The version is stored in lower case and requested in the case of the nuspec: it must be found.
        using var deleted = await client2.DeleteAsync("api/v2/package/Legacy.Pkg/5.7.22-Hdbfd3d9a85-b6");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await RowsAsync(connectionString, "Packages"));
    }

    // Package life cycle.

    [PostgreSqlFact]
    public async Task FirstPushOnAFreshDatabase_ThenRestoreSearchAndMetadata_Work()
    {
        // https://github.com/bagetter/BaGetter/issues/291: the first push on an empty PostgreSQL database failed with 500.
        var connectionString = await postgres.CreateDatabaseAsync();
        var app = Start(connectionString);
        using var client = app.CreateClient();
        var package = TestPackageBuilder.Create("Acme.Core", "1.0.0", dependencyId: "Acme.Base");

        using var pushed = await PushAsync(client, package);
        Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);

        // Restore: version list, then the file itself, byte for byte.
        Assert.Equal(["1.0.0"], Versions(await GetJsonAsync(client, "v3/package/acme.core/index.json")));
        var downloaded = await client.GetByteArrayAsync("v3/package/acme.core/1.0.0/acme.core.1.0.0.nupkg");
        Assert.Equal(package, downloaded);
        Assert.Equal(1, await postgres.ScalarAsync<long>(connectionString, "SELECT \"Downloads\" FROM \"Packages\""));

        // Search and autocomplete, by id, by part of the id and in another case.
        foreach (var query in new[] { "Acme.Core", "acme", "ACME.CORE" })
        {
            var search = await GetJsonAsync(client, $"v3/search?q={query}");
            Assert.Equal(1, search["totalHits"].GetValue<int>());
            Assert.Equal("Acme.Core", search["data"][0]["id"].GetValue<string>());
        }

        Assert.Equal(0, (await GetJsonAsync(client, "v3/search?q=nothing-like-it"))["totalHits"].GetValue<int>());
        Assert.Equal("Acme.Core", (await GetJsonAsync(client, "v3/autocomplete?q=acm"))["data"][0].GetValue<string>());
        Assert.Equal("1.0.0", (await GetJsonAsync(client, "v3/autocomplete?id=acme.core"))["data"][0].GetValue<string>());

        // Registration and metadata, with the dependency stored in its own table.
        var entry = (await GetJsonAsync(client, "v3/registration/acme.core/1.0.0.json"))["catalogEntry"];
        Assert.Equal("Acme.Core", entry["id"].GetValue<string>());
        Assert.Equal("Test author", entry["authors"].GetValue<string>());
        Assert.True(entry["listed"].GetValue<bool>());
        var dependency = entry["dependencyGroups"][0];
        Assert.Equal("net8.0", dependency["targetFramework"].GetValue<string>());
        Assert.Equal("Acme.Base", dependency["dependencies"][0]["id"].GetValue<string>());
        Assert.Equal(1, await RowsAsync(connectionString, "PackageDependencies"));
        Assert.Equal(1, (await GetJsonAsync(client, "v3/registration/acme.core/index.json"))["count"].GetValue<int>());

        // The same version again is a conflict, not a 500, and not a second row.
        using var duplicate = await PushAsync(client, package);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(1, await RowsAsync(connectionString, "Packages"));
    }

    [PostgreSqlFact]
    public async Task UnlistAndRelist_ChangeSearchAndMetadataButNotDownloads()
    {
        var app = Start(await postgres.CreateDatabaseAsync());
        using var client = app.CreateClient();
        using var pushed = await PushAsync(client, TestPackageBuilder.Create("Acme.Core", "1.0.0"));
        Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);

        // Default PackageDeletionBehavior is Unlist.
        using var unlisted = await client.DeleteAsync("api/v2/package/Acme.Core/1.0.0");
        Assert.Equal(HttpStatusCode.NoContent, unlisted.StatusCode);

        Assert.Equal(0, (await GetJsonAsync(client, "v3/search?q=acme"))["totalHits"].GetValue<int>());
        Assert.False((await GetJsonAsync(client, "v3/registration/acme.core/1.0.0.json"))["catalogEntry"]["listed"].GetValue<bool>());
        using (var download = await client.GetAsync("v3/package/acme.core/1.0.0/acme.core.1.0.0.nupkg"))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        }

        using var relisted = await client.PostAsync("api/v2/package/Acme.Core/1.0.0", new ByteArrayContent([]));
        Assert.Equal(HttpStatusCode.OK, relisted.StatusCode);

        Assert.Equal(1, (await GetJsonAsync(client, "v3/search?q=acme"))["totalHits"].GetValue<int>());
        Assert.True((await GetJsonAsync(client, "v3/registration/acme.core/1.0.0.json"))["catalogEntry"]["listed"].GetValue<bool>());

        using var unknown = await client.DeleteAsync("api/v2/package/Acme.Core/9.9.9");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [PostgreSqlFact]
    public async Task HardDelete_RemovesTheRowsAndTheFiles_AndTheVersionCanBePushedAgain()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var app = Start(connectionString, dict => dict["PackageDeletionBehavior"] = "HardDelete");
        using var client = app.CreateClient();
        var package = TestPackageBuilder.Create("Acme.Core", "1.0.0", dependencyId: "Acme.Base");
        using var pushed = await PushAsync(client, package);
        Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);
        Assert.Equal(1, await RowsAsync(connectionString, "PackageDependencies"));

        using var deleted = await client.DeleteAsync("api/v2/package/Acme.Core/1.0.0");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await RowsAsync(connectionString, "Packages"));
        Assert.Equal(0, await RowsAsync(connectionString, "PackageDependencies"));
        using (var gone = await client.GetAsync("v3/package/acme.core/1.0.0/acme.core.1.0.0.nupkg"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        Assert.Equal(0, (await GetJsonAsync(client, "v3/search?q=acme"))["totalHits"].GetValue<int>());
        using var again = await PushAsync(client, package);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [PostgreSqlFact]
    public async Task VersionsAndIdsAreCaseInsensitive_UpstreamIssue239()
    {
        // https://github.com/bagetter/BaGetter/issues/239: a version with an uppercase letter could not be deleted.
        var connectionString = await postgres.CreateDatabaseAsync();
        var app = Start(connectionString, dict => dict["PackageDeletionBehavior"] = "HardDelete");
        using var client = app.CreateClient();
        using var pushed = await PushAsync(client, TestPackageBuilder.Create("Acme.MixedCase", "2.0.0-Beta.1+Build.5"));
        Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);

        // The server stores the lower-case form; clients ask for the lower-case form (flat container) or for the
        // nuspec's own case (delete, relist).
        Assert.Equal("2.0.0-beta.1", await postgres.ScalarAsync<string>(connectionString, "SELECT \"Version\" FROM \"Packages\""));
        foreach (var url in new[]
        {
            "v3/package/acme.mixedcase/2.0.0-beta.1/acme.mixedcase.2.0.0-beta.1.nupkg",
            "v3/package/ACME.MIXEDCASE/2.0.0-BETA.1/acme.mixedcase.2.0.0-beta.1.nupkg",
        })
        {
            using var download = await client.GetAsync(url);
            Assert.True(download.StatusCode == HttpStatusCode.OK, $"GET {url} answered {(int)download.StatusCode}.");
        }

        Assert.Equal(["2.0.0-beta.1"], Versions(await GetJsonAsync(client, "v3/package/acme.mixedcase/index.json")));
        Assert.Equal("2.0.0-beta.1", (await GetJsonAsync(client, "v3/registration/ACME.MixedCase/2.0.0-Beta.1.json"))["catalogEntry"]["version"].GetValue<string>());

        using var deleted = await client.DeleteAsync("api/v2/package/ACME.MixedCase/2.0.0-Beta.1");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await RowsAsync(connectionString, "Packages"));

        // Two ids that differ by case are one package: the second push is a duplicate.
        using var first = await PushAsync(client, TestPackageBuilder.Create("Acme.Pkg", "1.0.0"));
        using var second = await PushAsync(client, TestPackageBuilder.Create("ACME.PKG", "1.0.0"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    // Restart.

    [PostgreSqlFact]
    public async Task RestartOnAnExistingDatabase_KeepsEveryPackage()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var storageRoot = new Dictionary<string, string>();
        void Configure(Dictionary<string, string> dict)
        {
            // The same package files for both runs: the first host chooses the path, the second one reuses it.
            if (storageRoot.TryGetValue("path", out var path))
            {
                dict["Storage:Path"] = path;
            }
            else
            {
                storageRoot["path"] = dict["Storage:Path"];
            }
        }

        await using (var first = Start(connectionString, Configure))
        {
            using var client = first.CreateClient();
            using var pushed = await PushAsync(client, TestPackageBuilder.Create("Acme.Core", "1.0.0"));
            Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);
            using var unlisted = await client.DeleteAsync("api/v2/package/Acme.Core/1.0.0");
            Assert.Equal(HttpStatusCode.NoContent, unlisted.StatusCode);
        }

        var history = await RowsAsync(connectionString, "__EFMigrationsHistory");
        var second = Start(connectionString, Configure);
        using var client2 = second.CreateClient();

        Assert.Equal(["1.0.0"], Versions(await GetJsonAsync(client2, "v3/package/acme.core/index.json")));
        Assert.False((await GetJsonAsync(client2, "v3/registration/acme.core/1.0.0.json"))["catalogEntry"]["listed"].GetValue<bool>());
        using var download = await client2.GetAsync("v3/package/acme.core/1.0.0/acme.core.1.0.0.nupkg");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        using var next = await PushAsync(client2, TestPackageBuilder.Create("Acme.Core", "1.1.0"));
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
        Assert.Equal(history, await RowsAsync(connectionString, "__EFMigrationsHistory"));
        Assert.Equal(2, await RowsAsync(connectionString, "Packages"));
    }

    // Load.

    [PostgreSqlFact]
    public async Task ManyConcurrentRequests_OnASmallConnectionPool_AllSucceedAndDoNotLeakConnections()
    {
        // https://github.com/bagetter/BaGetter/issues/229: connections were never returned and the server became
        // unusable. Here 120 requests share a pool of 5 connections, twice; a leak would time out on the pool.
        const int pool = 5;
        var connectionString = await postgres.CreateDatabaseAsync(maxPoolSize: pool);
        var app = Start(connectionString);
        using var client = app.CreateClient();
        for (var i = 0; i < 4; i++)
        {
            using var pushed = await PushAsync(client, TestPackageBuilder.Create("Acme.Load", $"1.0.{i}"));
            Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);
        }

        var urls = new[]
        {
            "v3/search?q=acme",
            "v3/autocomplete?q=acm",
            "v3/registration/acme.load/index.json",
            "v3/registration/acme.load/1.0.2.json",
            "v3/package/acme.load/index.json",
            "v3/package/acme.load/1.0.1/acme.load.1.0.1.nupkg",
            "health",
        };

        for (var wave = 0; wave < 2; wave++)
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 120).Select(async i =>
            {
                using var response = await client.GetAsync(urls[i % urls.Length]);
                return (Url: urls[i % urls.Length], response.StatusCode);
            }));

            Assert.All(results, r => Assert.True(r.StatusCode == HttpStatusCode.OK, $"{r.Url} answered {(int)r.StatusCode}."));

            var open = await postgres.ScalarAsync<long>(
                connectionString,
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()");
            Assert.True(open <= pool, $"{open} connections stay open after wave {wave}, the pool allows {pool}.");
        }
    }

    [PostgreSqlFact]
    public async Task ConcurrentPushes_OfDifferentVersionsAndOfTheSameVersion_NeverFailWithA500()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var app = Start(connectionString);
        using var client = app.CreateClient();

        var different = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            using var response = await PushAsync(client, TestPackageBuilder.Create("Acme.Parallel", $"1.0.{i}"));
            return response.StatusCode;
        }));
        Assert.All(different, status => Assert.Equal(HttpStatusCode.Created, status));
        Assert.Equal(8, await RowsAsync(connectionString, "Packages"));

        var same = TestPackageBuilder.Create("Acme.Same", "1.0.0");
        var racing = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var response = await PushAsync(client, same);
            return response.StatusCode;
        }));

        Assert.Equal(1, racing.Count(s => s == HttpStatusCode.Created));
        Assert.All(racing.Where(s => s != HttpStatusCode.Created), status => Assert.Equal(HttpStatusCode.Conflict, status));
        Assert.Equal(1, await postgres.ScalarAsync<long>(connectionString, "SELECT count(*) FROM \"Packages\" WHERE \"Id\" = 'Acme.Same'"));
    }

    // Health.

    [PostgreSqlFact]
    public async Task Health_ReportsThePostgreSqlCheck_AndFailsWhenTheDatabaseIsGone()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var app = Start(connectionString);
        using var admin = app.CreateClient();
        using var anonymous = app.CreateAnonymousClient();

        using (var liveness = await anonymous.GetAsync("health"))
        {
            Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        }

        var details = await GetJsonAsync(admin, "health/details");
        Assert.Equal("Healthy", details["PostgreSql"].GetValue<string>());
        Assert.Equal("Healthy", details["Status"].GetValue<string>());

        var database = new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Database;
        await postgres.ExecuteAsync(postgres.AdminConnectionString, $"DROP DATABASE \"{database}\" WITH (FORCE)");

        using var down = await anonymous.GetAsync("health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
    }
}
