using ElPaqueto.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Configuration;

/// <summary>
/// Refuses the authentication settings inherited from BaGetter, which ElPaqueto no longer reads.
/// </summary>
/// <remarks>
/// <para>
/// <c>ApiKey</c>, <c>Authentication:Credentials</c> and <c>Authentication:ApiKeys</c> configured the inherited
/// Basic handler and API key check, both replaced by the security module. Left in place they would do nothing,
/// and an operator would believe the feed is protected by them, or keep plain-text secrets for no purpose.
/// </para>
/// <para>
/// The settings are read from the configuration, not from the bound options, so that the failure names the
/// exact key paths whatever the source (file, environment variable, <c>/run/secrets</c>). An empty value, such
/// as the <c>"ApiKey": ""</c> that BaGetter shipped, is treated as absent. Messages never contain a value.
/// <c>Mirror:Authentication</c>, the credentials of the upstream feed, is a different section and is not affected.
/// </para>
/// </remarks>
internal sealed class LegacyAuthenticationSettingsValidator(IConfiguration configuration) : IValidateOptions<ElPaquetoOptions>
{
    /// <summary>The user documentation page that explains how to replace the legacy settings.</summary>
    internal const string UpgradeGuide = "docs/user/upgrading-from-bagetter.md";

    private const string ApiKeyPath = "ApiKey";

    private static readonly string[] LegacySections = ["Authentication:Credentials", "Authentication:ApiKeys"];

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ElPaquetoOptions options)
    {
        var paths = FindLegacySettings(configuration);
        if (paths.Count == 0)
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(paths.Select(path =>
            $"The setting '{path}' comes from BaGetter and is no longer supported: remove it. "
            + "Users and API keys are created with the 'admin' commands and stored, hashed, in the security database. "
            + $"See {UpgradeGuide}."));
    }

    /// <summary>
    /// Returns the configuration paths of the legacy settings that have a non-empty value, in a stable order.
    /// </summary>
    internal static IReadOnlyList<string> FindLegacySettings(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var paths = new List<string>();

        if (!string.IsNullOrEmpty(configuration[ApiKeyPath]))
        {
            paths.Add(ApiKeyPath);
        }

        foreach (var section in LegacySections)
        {
            paths.AddRange(configuration.GetSection(section)
                .AsEnumerable()
                .Where(pair => !string.IsNullOrEmpty(pair.Value))
                .Select(pair => pair.Key)
                .Order(StringComparer.OrdinalIgnoreCase));
        }

        return paths;
    }
}
