using System.Reflection;
using ElPaqueto.Security.Configuration;

namespace ElPaqueto.Security.Tests;

/// <summary>
/// Every setting of the <c>Security</c> section is documented in the user configuration reference
/// (docs/user/configuration.md), with its full key, so that a new option cannot ship undocumented.
/// </summary>
public class ConfigurationDocumentationTests
{
    [Fact]
    public void EverySecuritySetting_IsDocumentedInTheConfigurationReference()
    {
        var reference = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "user", "configuration.md"));

        var keys = SettingKeys(typeof(ElPaquetoSecurityOptions), ElPaquetoSecurityOptions.SectionName).ToList();
        var undocumented = keys.Where(key => !reference.Contains($"`{key}`", StringComparison.Ordinal)).ToList();

        // A sanity check of the reflection itself: the walk reaches nested sections and leaf settings.
        Assert.Contains("Security:PublicBaseUrl", keys);
        Assert.Contains("Security:Throttling:MaxConcurrentVerifications", keys);
        Assert.Empty(undocumented);
    }

    [Fact]
    public void SettingKeys_ReportsASettingMissingFromTheReference()
    {
        // The check above can fail: a key that is not in the text is reported.
        var keys = SettingKeys(typeof(ElPaquetoSecurityOptions), ElPaquetoSecurityOptions.SectionName);
        var reference = "`Security:PublicBaseUrl`";

        Assert.Contains(
            "Security:Database:ConnectionString",
            keys.Where(key => !reference.Contains($"`{key}`", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The configuration keys of the public settable properties of an options class: a leaf for a
    /// value or a list, a nested section for another options class of the security module.
    /// </summary>
    private static IEnumerable<string> SettingKeys(Type type, string prefix)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.SetMethod is not { IsPublic: true })
            {
                continue;
            }

            var key = $"{prefix}:{property.Name}";
            if (property.PropertyType.Namespace == typeof(ElPaquetoSecurityOptions).Namespace && property.PropertyType.IsClass)
            {
                foreach (var nested in SettingKeys(property.PropertyType, key))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return key;
            }
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ElPaqueto.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
