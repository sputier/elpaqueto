using System.ComponentModel.DataAnnotations;

namespace ElPaqueto.Security.Configuration;

/// <summary>
/// Location of the database that stores users, permissions and API keys (<c>Security:Database</c>).
/// </summary>
/// <remarks>
/// Validation messages name the setting but never repeat its value, because a connection string
/// may contain a secret.
/// </remarks>
public sealed class SecurityDatabaseOptions
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
}
