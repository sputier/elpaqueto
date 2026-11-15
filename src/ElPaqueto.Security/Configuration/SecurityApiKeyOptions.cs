using System.ComponentModel.DataAnnotations;

namespace ElPaqueto.Security.Configuration;

/// <summary>
/// API key settings (<c>Security:ApiKeys</c>).
/// </summary>
public sealed class SecurityApiKeyOptions
{
    /// <summary>
    /// Minimum time between two writes of an API key's last-use timestamp.
    /// Setting: <c>Security:ApiKeys:LastUsedUpdateInterval</c>.
    /// </summary>
    /// <remarks>
    /// Validating a key is a read; recording each use would turn every package download into a write
    /// on the security database and make requests contend for the SQLite writer lock. The timestamp is
    /// therefore written at most once per interval and per key, so it is accurate to within this value.
    /// </remarks>
    [Range(
        typeof(TimeSpan),
        "00:00:01",
        "1.00:00:00",
        ParseLimitsInInvariantCulture = true,
        ConvertValueInInvariantCulture = true,
        ErrorMessage = "Security:ApiKeys:LastUsedUpdateInterval must be between 00:00:01 and 1.00:00:00 (one day).")]
    public TimeSpan LastUsedUpdateInterval { get; set; } = TimeSpan.FromMinutes(5);
}
