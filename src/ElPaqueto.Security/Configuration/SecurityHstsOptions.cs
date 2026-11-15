using System.ComponentModel.DataAnnotations;

namespace ElPaqueto.Security.Configuration;

/// <summary>
/// HTTP Strict Transport Security (<c>Security:Hsts</c>).
/// </summary>
/// <remarks>
/// Off by default: TLS is usually terminated by a reverse proxy, which may already send the header,
/// and a mistaken HSTS policy is cached by browsers for its whole duration.
/// </remarks>
public sealed class SecurityHstsOptions
{
    /// <summary>The longest accepted <see cref="MaxAgeSeconds"/>: two years.</summary>
    public const int MaximumMaxAgeSeconds = 63_072_000;

    /// <summary>
    /// Whether the server sends <c>Strict-Transport-Security</c> on responses to requests whose
    /// effective scheme is HTTPS. Setting: <c>Security:Hsts:Enabled</c>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How long browsers remember the policy, in seconds. Setting: <c>Security:Hsts:MaxAgeSeconds</c>.
    /// Default one year; <c>0</c> tells browsers to forget a policy they remembered.
    /// </summary>
    [Range(0, MaximumMaxAgeSeconds, ErrorMessage = "Security:Hsts:MaxAgeSeconds must be between 0 and 63072000 (two years).")]
    public int MaxAgeSeconds { get; set; } = 31_536_000;

    /// <summary>
    /// Whether the policy also covers every subdomain of the server's host.
    /// Setting: <c>Security:Hsts:IncludeSubDomains</c>.
    /// </summary>
    public bool IncludeSubDomains { get; set; }
}
