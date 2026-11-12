using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Configuration;

/// <summary>
/// Rejects a plain value where the configuration expects a section. The configuration binder
/// silently ignores such a value and keeps the defaults, so the server would start against a
/// different database than the operator intended.
/// </summary>
internal sealed class SecurityConfigurationShapeValidator(IConfiguration configuration)
    : IValidateOptions<ElPaquetoSecurityOptions>
{
    public ValidateOptionsResult Validate(string? name, ElPaquetoSecurityOptions options)
    {
        var databaseSection = configuration.GetSection($"{ElPaquetoSecurityOptions.SectionName}:Database");

        if (databaseSection.Value is not null)
        {
            return ValidateOptionsResult.Fail(
                "Security:Database is a section, not a value: set Security:Database:ConnectionString.");
        }

        return ValidateOptionsResult.Success;
    }
}
