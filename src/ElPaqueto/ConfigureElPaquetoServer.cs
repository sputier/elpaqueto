using ElPaqueto.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace ElPaqueto;

public class ConfigureElPaquetoServer
    : IConfigureOptions<CorsOptions>
    , IConfigureOptions<FormOptions>
    , IConfigureOptions<ForwardedHeadersOptions>
    , IConfigureOptions<IISServerOptions>
{
    public const string CorsPolicy = "AllowAll";
    private readonly ElPaquetoOptions _elPaquetoOptions;

    public ConfigureElPaquetoServer(IOptions<ElPaquetoOptions> elPaquetoOptions)
    {
        _elPaquetoOptions = elPaquetoOptions.Value;
    }


    public void Configure(CorsOptions options)
    {
        // TODO: Consider disabling this on production builds.
        options.AddPolicy(
            CorsPolicy,
            builder => builder.AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader());
    }

    public void Configure(FormOptions options)
    {
        // Allow packages up to ~8GiB in size
        options.MultipartBodyLengthLimit = (long) _elPaquetoOptions.MaxPackageSizeGiB * int.MaxValue / 2;
    }

    public void Configure(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

        // Do not restrict to local network/proxy
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    }

    public void Configure(IISServerOptions options)
    {
        options.MaxRequestBodySize = (long)_elPaquetoOptions.MaxPackageSizeGiB * int.MaxValue / 2;
    }
}
