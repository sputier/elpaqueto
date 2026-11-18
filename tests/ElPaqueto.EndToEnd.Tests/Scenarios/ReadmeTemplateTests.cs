using System.Net;
using System.Text.Json;
using ElPaqueto.EndToEnd.Tests.Harness;

namespace ElPaqueto.EndToEnd.Tests.Scenarios;

/// <summary>
/// #43: Visual Studio (2022 17.13 and later, 2026) shows the readme tab of a package only when the service
/// index advertises <c>ReadmeUriTemplate/6.13.0</c> (upstream bagetter/BaGetter#289: the endpoint existed, the
/// resource did not). Checked here as a client does: read the template from <c>v3/index.json</c>, expand its tokens.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class ReadmeTemplateTests : EndToEndTest<ReadmeTemplateTests.Setup>, IClassFixture<ReadmeTemplateTests.Setup>
{
    public ReadmeTemplateTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task ServiceIndex_AdvertisesTheReadmeTemplate_AndItsExpansionServesTheReadmeToAReader()
    {
        var package = Fixture.Packages.Base200;
        using var index = JsonDocument.Parse(await ReadAsync(Fixture.Server.ServiceIndex, Fixture.Reader, HttpStatusCode.OK));
        var template = index.RootElement.GetProperty("resources").EnumerateArray()
            .Single(r => r.GetProperty("@type").GetString() == "ReadmeUriTemplate/6.13.0")
            .GetProperty("@id").GetString()!;
        Output.WriteLine("ReadmeUriTemplate/6.13.0: " + template);
        Assert.StartsWith(Fixture.Server.BaseAddress.AbsoluteUri, template, StringComparison.Ordinal);

        var url = new Uri(template
            .Replace("{lower_id}", package.Id.ToLowerInvariant(), StringComparison.Ordinal)
            .Replace("{lower_version}", package.Version.ToLowerInvariant(), StringComparison.Ordinal));

        var readme = await ReadAsync(url, Fixture.Reader, HttpStatusCode.OK);
        Assert.Contains($"# {package.Id} {package.Version}", readme, StringComparison.Ordinal);
        await ReadAsync(url, credentials: null, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ReadmeUrl_ForAnUnknownPackage_Answers404ToAReader()
    {
        var url = new Uri(Fixture.Server.BaseAddress, "v3/package/elpaqueto.e2e.unknown/1.0.0/readme");

        await ReadAsync(url, Fixture.Reader, HttpStatusCode.NotFound);
    }

    private async Task<string> ReadAsync(Uri url, UserCredential? credentials, HttpStatusCode expected)
    {
        using var response = await Fixture.Api.SendAsync(ServerApi.Request(HttpMethod.Get, url, credentials));
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    public sealed class Setup : ServerFixture
    {
        protected override Task SeedAsync()
            => Api.EnsurePushedAsync(PublishKey, Packages.Base200);
    }
}
