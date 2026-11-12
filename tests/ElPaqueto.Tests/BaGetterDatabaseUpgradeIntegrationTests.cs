using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// An existing database keeps working after the rename when the connection string points to it (issue #59):
/// the schema and the migration ids did not change, only the default file name.
/// </summary>
public sealed class BaGetterDatabaseUpgradeIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root = Directory.CreateTempSubdirectory("elpaqueto-bagetter-db-").FullName;

    public BaGetterDatabaseUpgradeIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The temp directory is disposable.
        }
    }

    [Fact]
    public async Task ADatabaseNamedBaGetterDb_IsServedWhenTheConnectionStringPointsToIt()
    {
        var database = Path.Combine(_root, "bagetter.db");
        var storage = Path.Combine(_root, "packages");

        using (var before = Create(database, storage))
        {
            await before.AddPackageAsync(TestResources.GetResourceStream(TestResources.Package));
        }

        SqliteConnection.ClearAllPools();
        Assert.True(File.Exists(database));

        using var after = Create(database, storage);
        using var client = after.CreateClient();
        using var response = await client.GetAsync("v3/package/testdata/index.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("1.2.3", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "elpaqueto.db")));
    }

    private ElPaquetoApplication Create(string database, string storage) => new(_output, inMemoryConfiguration: dict =>
    {
        dict["Database:ConnectionString"] = $"Data Source={database}";
        dict["Storage:Path"] = storage;
    });
}
