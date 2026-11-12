using System.ComponentModel.DataAnnotations;
using Microsoft.Data.Sqlite;

namespace ElPaqueto.Security.Configuration;

/// <summary>
/// Location of the database that stores users, permissions and API keys (<c>Security:Database</c>).
/// </summary>
/// <remarks>
/// Validation messages name the setting but never repeat its value, because a connection string
/// may contain a secret.
/// </remarks>
public sealed class SecurityDatabaseOptions : IValidatableObject
{
    /// <summary>
    /// Maximum accepted length of the connection string.
    /// </summary>
    public const int MaxConnectionStringLength = 1024;

    /// <summary>
    /// The SQLite connection string of the security database.
    /// Setting: <c>Security:Database:ConnectionString</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Security:Database:ConnectionString is required and must not be empty.")]
    [StringLength(MaxConnectionStringLength, ErrorMessage = "Security:Database:ConnectionString is too long.")]
    public string ConnectionString { get; set; } = "Data Source=auth.db";

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        string? problem = null;

        try
        {
            var builder = new SqliteConnectionStringBuilder(ConnectionString);
            if (string.IsNullOrWhiteSpace(builder.DataSource))
            {
                problem = "Security:Database:ConnectionString must specify a Data Source.";
            }
        }
        catch (ArgumentException)
        {
            // The exception message can quote part of the connection string: report a fixed message.
            problem = "Security:Database:ConnectionString is not a valid SQLite connection string.";
        }

        if (problem is not null)
        {
            yield return new ValidationResult(problem);
        }
    }
}
