using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace ElPaqueto.Tests;

// ElPaqueto addition (issue #41), kept out of the inherited file so that upstream changes to it still apply. Both
// apps of the inherited tests enforce authentication: the downstream refuses anonymous callers, and the upstream
// refuses a mirror that does not authenticate, so the inherited tests pass only because the mirror sends credentials.
public partial class MirrorIntegrationTests
{
    [Fact]
    public async Task TheSameRequestWithoutCredentials_Returns401OnTheDownstream()
    {
        await _upstream.AddPackageAsync(_packageStream);
        using var anonymous = _downstream.CreateAnonymousClient();

        using var authenticated = await _downstreamClient.GetAsync("v3/package/TestData/1.2.3/TestData.1.2.3.nupkg");
        using var refused = await anonymous.GetAsync("v3/package/TestData/1.2.3/TestData.1.2.3.nupkg");

        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task AMirrorWithoutCredentials_GetsNothingFromTheUpstream()
    {
        await _upstream.AddPackageAsync(_packageStream);
        using var anonymousUpstream = _upstream.CreateAnonymousClient();
        using var downstream = new ElPaquetoApplication(new NullTestOutput(), anonymousUpstream);
        using var client = downstream.CreateClient();

        using var direct = await anonymousUpstream.GetAsync("v3/package/TestData/index.json");
        using var mirrored = await client.GetAsync("v3/package/TestData/1.2.3/TestData.1.2.3.nupkg");

        // Control: the same request through the mirror of the class, whose upstream client authenticates.
        using var authenticatedMirror = await _downstreamClient.GetAsync("v3/package/TestData/1.2.3/TestData.1.2.3.nupkg");

        Assert.Equal(HttpStatusCode.Unauthorized, direct.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, mirrored.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticatedMirror.StatusCode);
    }
}
