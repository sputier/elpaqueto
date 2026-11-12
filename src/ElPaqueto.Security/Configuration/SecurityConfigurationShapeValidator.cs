using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Configuration;

/// <summary>
/// Rejects a plain value where the configuration expects a section. The configuration binder
/// silently ignores such a value and keeps the defaults, so the server would start with settings
/// the operator did not intend.
/// </summary>
internal sealed class SecurityConfigurationShapeValidator(IConfiguration configuration)
    : IValidateOptions<ElPaquetoSecurityOptions>
{
    private static readonly string[] Sections = ["Database", "Passwords", "Authentication", "ApiKeys", "ReverseProxy", "Transport", "Cors", "Hsts"];

    // A plain value where a list is expected is silently ignored by the binder too.
    private static readonly string[] Lists = [
        "ReverseProxy:KnownProxies",
        "ReverseProxy:KnownNetworks",
        "Cors:AllowedOrigins",
        "Cors:AllowedMethods",
        "Cors:AllowedHeaders",
    ];

    public ValidateOptionsResult Validate(string? name, ElPaquetoSecurityOptions options)
    {
        var failures = new List<string>();

        foreach (var section in Sections)
        {
            var path = $"{ElPaquetoSecurityOptions.SectionName}:{section}";

            if (configuration.GetSection(path).Value is not null)
            {
                failures.Add($"{path} is a section, not a value: set its keys, for example {path}:<key>.");
            }
        }

        foreach (var list in Lists)
        {
            var path = $"{ElPaquetoSecurityOptions.SectionName}:{list}";

            if (configuration.GetSection(path).Value is not null)
            {
                failures.Add($"{path} is a list, not a value: set its entries, for example {path}:0, {path}:1.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
