using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// The generated packages of the PostgreSQL tests must be accepted by the real indexing pipeline: this runs on the
/// default SQLite host, so a broken helper is noticed without Docker.
/// </summary>
public class TestPackageBuilderTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AGeneratedPackage_WithAMixedCaseVersionAndADependency_IsAccepted()
    {
        await using var app = new ElPaquetoApplication(output);
        using var client = app.CreateClient();
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(TestPackageBuilder.Create("Acme.Core", "2.0.0-Beta.1", dependencyId: "Acme.Base")), "package", "package.nupkg");

        using var pushed = await client.PutAsync("api/v2/package", content);
        using var registration = await client.GetAsync("v3/registration/acme.core/index.json");
        var body = await registration.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        Assert.Contains("Acme.Base", body, System.StringComparison.Ordinal);
        Assert.Contains("2.0.0-beta.1", body, System.StringComparison.Ordinal);
    }
}
