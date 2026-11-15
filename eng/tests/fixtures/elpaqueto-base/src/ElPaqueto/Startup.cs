using System;
using ElPaqueto.Core;
using Microsoft.Extensions.Options;

namespace ElPaqueto;

public class Startup
{
    private readonly ElPaquetoOptions _elPaquetoOptions;

    public Startup(IOptions<ElPaquetoOptions> elPaquetoOptions)
    {
        _elPaquetoOptions = elPaquetoOptions.Value ?? throw new ArgumentNullException(nameof(elPaquetoOptions));
    }

    public string ConfigRoot => Environment.GetEnvironmentVariable("ELPAQUETO_CONFIG_ROOT");

    public string DatabaseFile => "elpaqueto.db";

    public string Analyzer => "baget-exact-match-analyzer";

    public void Configure()
    {
        var elpaqueto = new ElPaquetoEndpointBuilder();
        elpaqueto.MapEndpoints();
    }
}
