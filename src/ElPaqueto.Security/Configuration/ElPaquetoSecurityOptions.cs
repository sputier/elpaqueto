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

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Database is null)
        {
            yield return new ValidationResult("Security:Database is required.");
            yield break;
        }

        var nested = new List<ValidationResult>();
        Validator.TryValidateObject(Database, new ValidationContext(Database), nested, validateAllProperties: true);

        foreach (var result in nested)
        {
            yield return result;
        }
    }
}
