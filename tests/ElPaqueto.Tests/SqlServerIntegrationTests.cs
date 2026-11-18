using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ElPaqueto.Core;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Versioning;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// The real host (the same pipeline as every other integration test) with <c>Database:Type=SqlServer</c>, against a
/// real SQL Server 2022 started by Testcontainers. Each test works in its own empty database of one shared container.
/// The tests are skipped, with the reason, when no Docker daemon is available; the CI job sets
/// <c>ELPAQUETO_REQUIRE_DOCKER=1</c> so that they cannot be skipped there. See docs/technical/testing.md.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "SqlServer")]
public sealed class SqlServerIntegrationTests(SqlServerFixture sql, ITestOutputHelper output) : IAsyncLifetime
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
                dict["Database:Type"] = "SqlServer";
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

    private static async Task<JsonNode> CatalogEntryAsync(HttpClient client, string id)
        => (await GetJsonAsync(client, $"v3/registration/{id}/index.json"))["items"][0]["items"][0]["catalogEntry"];

    private static string[] Versions(JsonNode flatContainerIndex)
        => flatContainerIndex["versions"].AsArray().Select(v => v.GetValue<string>()).ToArray();

    private Task<long> RowsAsync(string connectionString, string table)
        => sql.ScalarAsync<long>(connectionString, $"SELECT count(*) FROM [{table}]");

    [SqlServerFact]
    public async Task Migrations_OnAnEmptyDatabase_CreateTheWholeSchema()
    {
        var connectionString = await sql.CreateDatabaseAsync();
        Assert.Equal(0, await sql.ScalarAsync<long>(connectionString, "SELECT count(*) FROM sys.tables"));
        var app = Start(connectionString);

        using var client = app.CreateClient();
        using var response = await client.GetAsync("v3/index.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IContext>();
        var known = context.Database.GetMigrations().Count();
        Assert.True(known >= 1, "The SQL Server provider should carry its migrations.");
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(known, await RowsAsync(connectionString, "__EFMigrationsHistory"));
        foreach (var table in new[] { "Packages", "PackageDependencies", "PackageTypes", "TargetFrameworks" })
        {
            Assert.Equal(1, await sql.ScalarAsync<long>(connectionString, $"SELECT count(*) FROM sys.tables WHERE name = '{table}'"));
        }

        // The unique index that makes a duplicate version fail in the database.
        Assert.Equal(1, await sql.ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM sys.indexes WHERE name = 'IX_Packages_Id_Version' AND is_unique = 1"));
    }

    [SqlServerFact]
    public async Task FirstPush_ThenRestoreSearchAndMetadata_Work()
    {
        var connectionString = await sql.CreateDatabaseAsync();
        var app = Start(connectionString);
        using var client = app.CreateClient();
        var package = TestPackageBuilder.Create("Acme.Core", "1.0.0", dependencyId: "Acme.Base");

        using var pushed = await PushAsync(client, package);
        Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);

        // Restore: version list, then the file itself, byte for byte, and the download counter.
        Assert.Equal(["1.0.0"], Versions(await GetJsonAsync(client, "v3/package/acme.core/index.json")));
        var downloaded = await client.GetByteArrayAsync("v3/package/acme.core/1.0.0/acme.core.1.0.0.nupkg");
        Assert.Equal(package, downloaded);
        Assert.Equal(1, await sql.ScalarAsync<long>(connectionString, "SELECT [Downloads] FROM [Packages]"));

        // Search and autocomplete, by id, by part of the id and in another case (the default collation is case-insensitive).
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
        var entry = await CatalogEntryAsync(client, "acme.core");
        Assert.Equal("Acme.Core", entry["id"].GetValue<string>());
        Assert.Equal("Test author", entry["authors"].GetValue<string>());
        Assert.True(entry["listed"].GetValue<bool>());
        var dependency = entry["dependencyGroups"][0];
        Assert.Equal("net8.0", dependency["targetFramework"].GetValue<string>());
        Assert.Equal("Acme.Base", dependency["dependencies"][0]["id"].GetValue<string>());
        Assert.Equal(1, await RowsAsync(connectionString, "PackageDependencies"));
    }

    [SqlServerFact]
    public async Task DuplicatePush_AnswersConflict_AndStoresASingleRow()
    {
        var connectionString = await sql.CreateDatabaseAsync();
        var app = Start(connectionString);
        using var client = app.CreateClient();
        var package = TestPackageBuilder.Create("Acme.Core", "1.0.0");
        using var pushed = await PushAsync(client, package);
        Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);

        using var duplicate = await PushAsync(client, package);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // The same version in another case is the same version for SQL Server's default collation.
        using var otherCase = await PushAsync(client, TestPackageBuilder.Create("ACME.CORE", "1.0.0"));
        Assert.Equal(HttpStatusCode.Conflict, otherCase.StatusCode);
        Assert.Equal(1, await RowsAsync(connectionString, "Packages"));
    }

    [SqlServerFact]
    public async Task AUniqueIndexViolation_IsRecognisedAsAnAlreadyExistingPackage()
    {
        // The push endpoint checks for an existing version first; the database detection of a duplicate
        // (SqlException numbers) only matters when two pushes race. Exercise it directly, bypassing the check.
        var connectionString = await sql.CreateDatabaseAsync();
        var app = Start(connectionString);
        using var client = app.CreateClient();
        using var pushed = await PushAsync(client, TestPackageBuilder.Create("Acme.Core", "1.0.0"));
        Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);

        static Package Duplicate() => new()
        {
            Id = "Acme.Core",
            Version = NuGetVersion.Parse("1.0.0"),
            Authors = [],
            Tags = [],
            Published = DateTime.UtcNow,
            Listed = true,
            Dependencies = [],
            PackageTypes = [],
            TargetFrameworks = [],
        };

        // The raw exception: SqlClient's SqlException, with the number of a unique index violation (2601; a unique
        // constraint would be 2627). The number is what SqlServerContext matches on.
        using (var rawScope = app.Services.CreateScope())
        {
            var context = rawScope.ServiceProvider.GetRequiredService<IContext>();
            context.Packages.Add(Duplicate());
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(default));
            var sqlException = Assert.IsType<SqlException>(failure.GetBaseException());
            Assert.Contains(sqlException.Errors.OfType<SqlError>(), e => e.Number is 2601 or 2627);
            Assert.True(context.IsUniqueConstraintViolationException(failure));
        }

        using var scope = app.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IPackageDatabase>();
        Assert.Equal(PackageAddResult.PackageAlreadyExists, await database.AddAsync(Duplicate(), default));
        Assert.Equal(1, await RowsAsync(connectionString, "Packages"));
    }

    [SqlServerFact]
    public async Task UnlistAndRelist_ChangeSearchAndMetadataButNotDownloads()
    {
        var app = Start(await sql.CreateDatabaseAsync());
        using var client = app.CreateClient();
        using var pushed = await PushAsync(client, TestPackageBuilder.Create("Acme.Core", "1.0.0"));
        Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);

        // Default PackageDeletionBehavior is Unlist.
        using var unlisted = await client.DeleteAsync("api/v2/package/Acme.Core/1.0.0");
        Assert.Equal(HttpStatusCode.NoContent, unlisted.StatusCode);

        Assert.Equal(0, (await GetJsonAsync(client, "v3/search?q=acme"))["totalHits"].GetValue<int>());
        Assert.False((await CatalogEntryAsync(client, "acme.core"))["listed"].GetValue<bool>());
        using (var download = await client.GetAsync("v3/package/acme.core/1.0.0/acme.core.1.0.0.nupkg"))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        }

        using var relisted = await client.PostAsync("api/v2/package/Acme.Core/1.0.0", new ByteArrayContent([]));
        Assert.Equal(HttpStatusCode.OK, relisted.StatusCode);

        Assert.Equal(1, (await GetJsonAsync(client, "v3/search?q=acme"))["totalHits"].GetValue<int>());
        Assert.True((await CatalogEntryAsync(client, "acme.core"))["listed"].GetValue<bool>());

        using var unknown = await client.DeleteAsync("api/v2/package/Acme.Core/9.9.9");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [SqlServerFact]
    public async Task HardDelete_RemovesTheRowsAndTheFiles_AndTheVersionCanBePushedAgain()
    {
        var connectionString = await sql.CreateDatabaseAsync();
        var app = Start(connectionString, dict => dict["PackageDeletionBehavior"] = "HardDelete");
        using var client = app.CreateClient();
        var package = TestPackageBuilder.Create("Acme.Core", "1.0.0", dependencyId: "Acme.Base");
        using var pushed = await PushAsync(client, package);
        Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);
        Assert.Equal(1, await RowsAsync(connectionString, "PackageDependencies"));

        using var deleted = await client.DeleteAsync("api/v2/package/Acme.Core/1.0.0");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await RowsAsync(connectionString, "Packages"));
        Assert.Equal(0, await sql.ScalarAsync<long>(connectionString, "SELECT count(*) FROM [PackageDependencies] WHERE [PackageKey] IS NOT NULL"));
        using (var gone = await client.GetAsync("v3/package/acme.core/1.0.0/acme.core.1.0.0.nupkg"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        Assert.Equal(0, (await GetJsonAsync(client, "v3/search?q=acme"))["totalHits"].GetValue<int>());
        using var again = await PushAsync(client, package);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [SqlServerFact]
    public async Task RestartOnAnExistingDatabase_KeepsEveryPackage()
    {
        var connectionString = await sql.CreateDatabaseAsync();
        // The same package files for both runs, in a directory the test owns: disposing the first host deletes
        // the temporary directories it created, including its default storage path.
        var storage = System.IO.Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ElPaquetoTests", Guid.NewGuid().ToString("N"), "Packages")).FullName;
        try
        {
            void Configure(Dictionary<string, string> dict) => dict["Storage:Path"] = storage;

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
            Assert.False((await CatalogEntryAsync(client2, "acme.core"))["listed"].GetValue<bool>());
            using var download = await client2.GetAsync("v3/package/acme.core/1.0.0/acme.core.1.0.0.nupkg");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            using var next = await PushAsync(client2, TestPackageBuilder.Create("Acme.Core", "1.1.0"));
            Assert.Equal(HttpStatusCode.Created, next.StatusCode);
            Assert.Equal(history, await RowsAsync(connectionString, "__EFMigrationsHistory"));
            Assert.Equal(2, await RowsAsync(connectionString, "Packages"));
        }
        finally
        {
            TestDirectory.DeleteQuietly(System.IO.Path.GetDirectoryName(storage));
        }
    }

    [SqlServerFact]
    public async Task ManyConcurrentRequests_OnASmallConnectionPool_AllSucceed()
    {
        var connectionString = await sql.CreateDatabaseAsync(maxPoolSize: 5);
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
            "v3/package/acme.load/index.json",
            "v3/package/acme.load/1.0.1/acme.load.1.0.1.nupkg",
        };

        for (var wave = 0; wave < 2; wave++)
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 60).Select(async i =>
            {
                using var response = await client.GetAsync(urls[i % urls.Length]);
                return (Url: urls[i % urls.Length], response.StatusCode);
            }));

            Assert.All(results, r => Assert.True(r.StatusCode == HttpStatusCode.OK, $"{r.Url} answered {(int)r.StatusCode}."));
        }

        // The downloads of 1.0.1 ran at once on a row-versioned table, and every one of them is counted: the counter
        // is incremented atomically in the database, so it neither conflicts on the row version nor loses updates.
        var downloads = 2 * Enumerable.Range(0, 60).Count(i => urls[i % urls.Length].EndsWith(".nupkg", StringComparison.Ordinal));
        var counted = await sql.ScalarAsync<long>(connectionString, "SELECT [Downloads] FROM [Packages] WHERE [Version] = '1.0.1'");
        Assert.Equal(downloads, counted);
    }
}
