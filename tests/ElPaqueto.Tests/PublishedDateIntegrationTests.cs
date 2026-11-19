using System;
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
/// The publication date is stored in UTC. It must come back as UTC whatever the time zone of the server is,
/// otherwise the registration API reports a shifted date on a server that does not run in UTC.
/// </summary>
public sealed class PublishedDateIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PublishedDate_IsReadBackAsUtc()
    {
        await using var app = new ElPaquetoApplication(output);
        using var client = app.CreateClient();
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(TestPackageBuilder.Create("Acme.Dated", "1.0.0")), "package", "package.nupkg");
        using var pushed = await client.PutAsync("api/v2/package", content);
        Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);

        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IContext>();
        var published = await context.Packages.Where(p => p.Id == "Acme.Dated").Select(p => p.Published).SingleAsync();

        Assert.Equal(DateTimeKind.Utc, published.Kind);
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), published);
    }
}
