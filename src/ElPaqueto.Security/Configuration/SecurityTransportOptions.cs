namespace ElPaqueto.Security.Configuration;

/// <summary>
/// Transport policy (<c>Security:Transport</c>).
/// </summary>
public sealed class SecurityTransportOptions
{
    /// <summary>
    /// Whether requests from non-loopback clients must use HTTPS. A remote plain-HTTP request is
    /// answered with 403 before authentication, never redirected. The scheme and the client address
    /// are taken after trusted forwarded headers. Setting: <c>Security:Transport:RequireHttps</c>.
    /// </summary>
    /// <remarks>
    /// Disabling it is meant for isolated test networks only, and logs a warning at startup.
    /// </remarks>
    public bool RequireHttps { get; set; } = true;
}
