using System.ComponentModel.DataAnnotations;

namespace ElPaqueto.Security.Configuration;

/// <summary>
/// Root options of the security module, bound from the <c>Security</c> configuration section.
/// </summary>
/// <remarks>
/// Each concern is a nested class. Later changes add their own nested classes and keys.
/// Values come from the usual configuration sources: appsettings, environment variables
/// (<c>Security__Database__ConnectionString</c>), command-line arguments and, with the highest
/// precedence, files in <c>/run/secrets</c> (Docker secrets).
/// </remarks>
public sealed class ElPaquetoSecurityOptions : IValidatableObject
{
    /// <summary>
    /// The name of the configuration section.
    /// </summary>
    public const string SectionName = "Security";

    /// <summary>
    /// The security database. Section: <c>Security:Database</c>.
    /// </summary>
    public SecurityDatabaseOptions Database { get; set; } = new();

    /// <summary>
    /// Password hashing and policy. Section: <c>Security:Passwords</c>.
    /// </summary>
    public SecurityPasswordOptions Passwords { get; set; } = new();

    /// <summary>
    /// API keys. Section: <c>Security:ApiKeys</c>.
    /// </summary>
    public SecurityApiKeyOptions ApiKeys { get; set; } = new();

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in ValidateSection(Database, nameof(Database)))
        {
            yield return result;
        }

        foreach (var result in ValidateSection(Passwords, nameof(Passwords)))
        {
            yield return result;
        }

        foreach (var result in ValidateSection(ApiKeys, nameof(ApiKeys)))
        {
            yield return result;
        }
    }

    private static List<ValidationResult> ValidateSection(object? section, string name)
    {
        var results = new List<ValidationResult>();

        if (section is null)
        {
            results.Add(new ValidationResult($"{SectionName}:{name} is required."));
            return results;
        }

        Validator.TryValidateObject(section, new ValidationContext(section), results, validateAllProperties: true);
        return results;
    }
}
