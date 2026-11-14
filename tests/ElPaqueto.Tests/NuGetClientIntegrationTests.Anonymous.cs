using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using Xunit;

namespace ElPaqueto.Tests;

// ElPaqueto addition (issue #41), kept out of the inherited file so that upstream changes to it still apply: the
// official NuGet client, given the same source without credentials, is refused with 401.
public partial class NuGetClientIntegrationTests
{
    [Fact]
    public async Task TheSameRequestWithoutCredentials_Returns401()
    {
        using var anonymousClient = _app.CreateAnonymousClient();
        var providers = new List<Lazy<INuGetResourceProvider>>
        {
            new(() => new HttpSourceResourceProviderTestHost(anonymousClient)),
        };
        providers.AddRange(Repository.Provider.GetCoreV3());
        var anonymousRepository = new SourceRepository(new PackageSource(new Uri(_app.Server.BaseAddress, "v3/index.json").AbsoluteUri), providers);

        var index = await _repository.GetResourceAsync<ServiceIndexResourceV3>();
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => anonymousRepository.GetResourceAsync<ServiceIndexResourceV3>());

        Assert.NotNull(index);
        Assert.Contains("401", Flatten(refused), StringComparison.Ordinal);
    }

    private static string Flatten(Exception exception)
    {
        var text = "";
        for (var current = exception; current is not null; current = current.InnerException)
        {
            text += current.GetType().Name + ": " + current.Message + "\n";
        }

        return text;
    }
}
