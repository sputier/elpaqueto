using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// Issue #26: once a caller holds the permission of a write route, the route answers exactly as upstream did,
/// error answers included. Authorization adds refusals; it must not change what an authorized publisher sees.
/// </summary>
public class WriteRouteUpstreamStatusIntegrationTests : IDisposable
{
    private readonly ElPaquetoApplication _app;
    private readonly HttpClient _publisher;

    public WriteRouteUpstreamStatusIntegrationTests(ITestOutputHelper output)
    {
        _app = new ElPaquetoApplication(output);

        // The default client: the test user's key, in X-NuGet-ApiKey, with Publish.
        _publisher = _app.CreateClient();
    }

    public void Dispose()
    {
        _publisher.Dispose();
        _app.Dispose();
    }

    [Fact]
    public async Task AnUploadWithoutAPackage_Is400()
    {
        using var response = await _publisher.PutAsync("api/v2/package", new ByteArrayContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnUploadOfSomethingThatIsNotAPackage_Is400()
    {
        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent("not a package"u8.ToArray()), "package", "package.nupkg" },
        };

        using var response = await _publisher.PutAsync("api/v2/package", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RelistingAMissingVersion_Is404()
    {
        using var response = await _publisher.PostAsync("api/v2/package/missing.package/9.9.9", new ByteArrayContent([]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeletingAMissingVersion_Is404()
    {
        using var response = await _publisher.DeleteAsync("api/v2/package/missing.package/9.9.9");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
