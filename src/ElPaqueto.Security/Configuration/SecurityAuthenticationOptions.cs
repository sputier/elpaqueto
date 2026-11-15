using System.ComponentModel.DataAnnotations;

namespace ElPaqueto.Security.Configuration;

/// <summary>
/// Authentication behaviour (<c>Security:Authentication</c>).
/// </summary>
public sealed class SecurityAuthenticationOptions
{
    /// <summary>The longest accepted cache lifetime.</summary>
    public const string MaximumVerificationCacheTtl = "01:00:00";

    /// <summary>
    /// How long a successful password verification is remembered in memory.
    /// Setting: <c>Security:Authentication:VerificationCacheTtl</c> (a time span, default 5 minutes).
    /// </summary>
    /// <remarks>
    /// NuGet clients send credentials on every request and a password hash is deliberately slow:
    /// without the cache each request would pay for one. A disabled user, changed permissions and a
    /// changed password are always read from the database on every request, so the cache only affects
    /// how long an unchanged, valid password stays cheap. Zero disables the cache.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00", MaximumVerificationCacheTtl, ErrorMessage = "Security:Authentication:VerificationCacheTtl must be between 00:00:00 and 01:00:00.")]
    public TimeSpan VerificationCacheTtl { get; set; } = TimeSpan.FromMinutes(5);
}
