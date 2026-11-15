using System.ComponentModel.DataAnnotations;

namespace ElPaqueto.Security.Configuration;

/// <summary>
/// Password hashing cost and password policy (<c>Security:Passwords</c>).
/// </summary>
public sealed class SecurityPasswordOptions
{
    /// <summary>The lowest iteration count accepted.</summary>
    public const int MinimumIterationCount = 100_000;

    /// <summary>The highest iteration count accepted: a higher value is a self-inflicted denial of service.</summary>
    public const int MaximumIterationCount = 10_000_000;

    /// <summary>The lowest minimum password length that can be configured.</summary>
    public const int MinimumLengthFloor = 12;

    /// <summary>The longest password accepted. Fixed: unbounded input is a denial-of-service vector.</summary>
    public const int MaximumLength = 256;

    /// <summary>
    /// PBKDF2-HMAC-SHA512 iterations used for new hashes. Setting: <c>Security:Passwords:IterationCount</c>.
    /// </summary>
    /// <remarks>
    /// The default follows the OWASP Password Storage Cheat Sheet. Hashes made with a lower count still
    /// verify and are re-hashed after a successful login.
    /// </remarks>
    [Range(MinimumIterationCount, MaximumIterationCount, ErrorMessage = "Security:Passwords:IterationCount must be between 100000 and 10000000.")]
    public int IterationCount { get; set; } = 210_000;

    /// <summary>
    /// Minimum password length, counted in characters after Unicode normalization.
    /// Setting: <c>Security:Passwords:MinimumLength</c>.
    /// </summary>
    [Range(MinimumLengthFloor, MaximumLength, ErrorMessage = "Security:Passwords:MinimumLength must be between 12 and 256.")]
    public int MinimumLength { get; set; } = 15;
}
