using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ElPaqueto.Protocol;
using Xunit;

namespace ElPaqueto.Tests;

// ElPaqueto addition (issue #41), kept out of the inherited file so that upstream changes to it still apply: the
// same client SDK calls, made without credentials, are refused with 401.
public partial class ElPaquetoClientIntegrationTests
{
    [Fact]
    public async Task TheSameRequestsWithoutCredentials_Return401()
    {
        await _app.AddPackageAsync(_packageStream);
        using var anonymousHttpClient = _app.CreateAnonymousClient();
        var anonymousFactory = new NuGetClientFactory(anonymousHttpClient, new Uri(_app.Server.BaseAddress, "v3/index.json").AbsoluteUri);

        var index = await _clientFactory.CreateServiceIndexClient().GetAsync();
        var results = await _client.SearchAsync();
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => anonymousFactory.CreateServiceIndexClient().GetAsync());

        Assert.Equal("3.0.0", index.Version);
        Assert.Single(results);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }
}
