using System;
using BaGetter.Core;
using Microsoft.Extensions.Options;

namespace BaGetter;

public class Startup
{
    private readonly BaGetterOptions _baGetterOptions;

    public Startup(IOptions<BaGetterOptions> baGetterOptions)
    {
        _baGetterOptions = baGetterOptions.Value;
    }

    public string ConfigRoot => Environment.GetEnvironmentVariable("BAGET_CONFIG_ROOT");

    public string DatabaseFile => "bagetter.db";

    public string Analyzer => "baget-exact-match-analyzer";

    public void Configure()
    {
        var baget = new BaGetterEndpointBuilder();
        baget.MapEndpoints();
    }
}
