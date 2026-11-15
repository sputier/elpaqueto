using System;
using ElPaqueto.Core;
using ElPaqueto.Core.Indexing;
using Microsoft.Extensions.Options;

namespace ElPaqueto;

public class Startup
{
    private readonly ElPaquetoOptions _elPaquetoOptions;

    public Startup(IOptions<ElPaquetoOptions> elPaquetoOptions)
    {
        _elPaquetoOptions = elPaquetoOptions.Value ?? throw new ArgumentNullException(nameof(elPaquetoOptions));
    }

    public string ConfigRoot => Environment.GetEnvironmentVariable("ELPAQUETO_CONFIG_ROOT") ?? AppContext.BaseDirectory;

    public string DatabaseFile => "elpaqueto.db";

    public const string Analyzer = "baget-exact-match-analyzer";

    public void Configure()
    {
        // See: https://github.com/bagetter/BaGetter/issues/297
        var elpaqueto = new ElPaquetoEndpointBuilder(_elPaquetoOptions);
        elpaqueto.MapEndpoints();
    }
}
