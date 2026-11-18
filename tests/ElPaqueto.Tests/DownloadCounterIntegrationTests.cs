using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ElPaqueto.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// The download counter of a package version on the default database (SQLite). The same guarantee is checked on
/// PostgreSQL and SQL Server by their integration tests.
/// </summary>
public sealed class DownloadCounterIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ConcurrentDownloadsOfOneVersion_AreAllCounted_AndOnlyOnThatVersion()
    {
        await using var app = new ElPaquetoApplication(output);
        using var client = app.CreateClient();
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(TestPackageBuilder.Create("Acme.Counted", version)), "package", "package.nupkg");
            using var pushed = await client.PutAsync("api/v2/package", content);
            Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);
        }

        // Read, increment and save lost increments when downloads ran at once; the counter is now one atomic update.
        const int downloads = 40;
        var statuses = await Task.WhenAll(Enumerable.Range(0, downloads).Select(async _ =>
        {
            using var response = await client.GetAsync("v3/package/acme.counted/1.0.0/acme.counted.1.0.0.nupkg");
            return response.StatusCode;
        }));
        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));

        // A version that does not exist counts nothing and does not fail.
        using (var missing = await client.GetAsync("v3/package/acme.counted/9.9.9/acme.counted.9.9.9.nupkg"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IContext>();
        var counted = await context.Packages
            .Where(p => p.Id == "Acme.Counted")
            .OrderBy(p => p.NormalizedVersionString)
            .Select(p => new { p.NormalizedVersionString, p.Downloads })
            .ToListAsync();

        Assert.Equal(2, counted.Count);
        Assert.Equal(("1.0.0", downloads), (counted[0].NormalizedVersionString, (int)counted[0].Downloads));
        Assert.Equal(("2.0.0", 0), (counted[1].NormalizedVersionString, (int)counted[1].Downloads));
    }
}
