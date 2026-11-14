using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace ElPaqueto.Tests;

// ElPaqueto addition (issue #41), kept out of the inherited file so that upstream changes to it still apply: the
// inherited tests above use the authenticated default client, and this proves that the same requests without
// credentials are refused, so the adaptation left no anonymous path.
public partial class ApiIntegrationTests
{
    [Theory]
    [InlineData("v3/index.json")]
    [InlineData("v3/search")]
    [InlineData("v3/autocomplete")]
    [InlineData("v3/package/TestData/index.json")]
    [InlineData("v3/package/TestData/1.2.3/TestData.1.2.3.nupkg")]
    [InlineData("v3/package/TestData/1.2.3/TestData.nuspec")]
    [InlineData("v3/registration/TestData/index.json")]
    [InlineData("v3/registration/TestData/1.2.3.json")]
    [InlineData("v3/dependents?packageId=TestData")]
    [InlineData("api/download/symbols/testdata.pdb/16F71ED8DD574AA2AD4A22D29E9C981Bffffffff/testdata.pdb")]
    public async Task TheSameRequestWithoutCredentials_Returns401(string path)
    {
        await _app.AddPackageAsync(_packageStream);
        await _app.AddSymbolPackageAsync(_symbolPackageStream);
        using var anonymous = _app.CreateAnonymousClient();

        using var authenticated = await _client.GetAsync(path);
        using var refused = await anonymous.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }
}
