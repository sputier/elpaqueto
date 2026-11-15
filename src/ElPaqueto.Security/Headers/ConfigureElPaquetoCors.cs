using ElPaqueto.Security.Configuration;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Headers;

/// <summary>
/// Builds the server's only CORS policy from <c>Security:Cors</c>.
/// </summary>
/// <remarks>
/// The policy lists exact origins and never allows credentials. With no configured origin it
/// approves nothing: the CORS middleware still answers preflight requests (with 204 and no CORS
/// header), and every other request goes on without CORS headers.
/// </remarks>
internal sealed class ConfigureElPaquetoCors(IOptions<ElPaquetoSecurityOptions> securityOptions) : IConfigureOptions<CorsOptions>
{
    /// <summary>The name of the policy applied by <c>UseElPaquetoCors</c>.</summary>
    internal const string PolicyName = "ElPaqueto";

    public void Configure(CorsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var cors = securityOptions.Value.Cors;

        options.AddPolicy(PolicyName, policy => policy
            .WithOrigins([.. cors.NormalizedOrigins])
            .WithMethods([.. cors.EffectiveMethods])
            .WithHeaders([.. cors.EffectiveHeaders])
            .DisallowCredentials());
    }
}
