using System;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using Xunit;

namespace ElPaqueto.Core.Tests;

public class ElPaquetoServiceIndexTests
{
    [Fact]
    public async Task GetAsync_AdvertisesTheReadmeTemplate_BelowThePackageContentResource()
    {
        // Visual Studio shows a readme tab only when the service index has ReadmeUriTemplate/6.13.0
        // (bagetter/BaGetter#289). The route already exists: v3/package/{id}/{version}/readme.
        var url = new Mock<IUrlGenerator>();
        url.Setup(u => u.GetPackageContentResourceUrl()).Returns("https://nuget.example.test/v3/package/");
        var target = new ElPaquetoServiceIndex(url.Object);

        var index = await target.GetAsync();

        var readme = Assert.Single(index.Resources, r => r.Type.StartsWith("ReadmeUriTemplate/", StringComparison.Ordinal));
        Assert.Equal("ReadmeUriTemplate/6.13.0", readme.Type);
        Assert.Equal("https://nuget.example.test/v3/package/{lower_id}/{lower_version}/readme", readme.ResourceUrl);
        Assert.Contains(index.Resources, r => r.Type == "PackageBaseAddress/3.0.0");
        Assert.Equal(index.Resources.Count, index.Resources.Select(r => r.Type).Distinct().Count());
    }
}
