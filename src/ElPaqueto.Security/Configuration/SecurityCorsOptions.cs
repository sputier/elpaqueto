using System.ComponentModel.DataAnnotations;

namespace ElPaqueto.Security.Configuration;

/// <summary>
/// Cross-origin access from browser scripts (<c>Security:Cors</c>).
/// </summary>
/// <remarks>
/// NuGet clients are not browsers and the web UI is served from the server's own origin, so the
/// default allows no cross-origin access at all. Credentials mode is never allowed: the browser
/// would otherwise attach the Basic credentials it cached for the web UI to requests made by the
/// scripts of an allowed origin. A script that needs to authenticate sets the
/// <c>Authorization</c> header itself.
/// </remarks>
public sealed class SecurityCorsOptions : IValidatableObject
{
    /// <summary>The methods used when <see cref="AllowedMethods"/> is empty.</summary>
    internal static readonly IReadOnlyList<string> DefaultMethods = ["GET", "HEAD"];

    /// <summary>The request headers used when <see cref="AllowedHeaders"/> is empty.</summary>
    internal static readonly IReadOnlyList<string> DefaultHeaders = ["Authorization"];

    /// <summary>The methods an allow-list may contain: the ones the server's routes accept.</summary>
    private static readonly string[] AcceptedMethods = ["GET", "HEAD", "PUT", "POST", "DELETE"];

    /// <summary>
    /// Origins whose scripts may call the server, such as <c>https://ui.example.test</c>.
    /// Setting: <c>Security:Cors:AllowedOrigins</c>. Empty (the default): no cross-origin access.
    /// </summary>
    public IList<string> AllowedOrigins { get; set; } = [];

    /// <summary>
    /// Methods allowed for cross-origin requests. Setting: <c>Security:Cors:AllowedMethods</c>.
    /// Empty (the default) means <c>GET</c> and <c>HEAD</c>.
    /// </summary>
    /// <remarks>
    /// Empty rather than pre-filled: the configuration binder appends to a pre-filled list, so a
    /// configured list could never remove a default entry.
    /// </remarks>
    public IList<string> AllowedMethods { get; set; } = [];

    /// <summary>
    /// Request headers allowed for cross-origin requests. Setting: <c>Security:Cors:AllowedHeaders</c>.
    /// Empty (the default) means <c>Authorization</c>.
    /// </summary>
    public IList<string> AllowedHeaders { get; set; } = [];

    /// <summary>
    /// The normalized allowed origins, such as <c>https://ui.example.test:8443</c>. An invalid entry is
    /// left out; validation stops the server before such options are used.
    /// </summary>
    internal IReadOnlyList<string> NormalizedOrigins =>
        [.. (AllowedOrigins ?? []).Select(o => TryNormalizeOrigin(o, out var origin) ? origin : null).OfType<string>()];

    /// <summary>The allowed methods, upper case, or <see cref="DefaultMethods"/>. Meant for validated options.</summary>
    internal IReadOnlyList<string> EffectiveMethods =>
        AllowedMethods is { Count: > 0 } ? [.. AllowedMethods.OfType<string>().Select(m => m.Trim().ToUpperInvariant())] : DefaultMethods;

    /// <summary>The allowed request headers, or <see cref="DefaultHeaders"/>. Meant for validated options.</summary>
    internal IReadOnlyList<string> EffectiveHeaders =>
        AllowedHeaders is { Count: > 0 } ? [.. AllowedHeaders.OfType<string>().Select(h => h.Trim())] : DefaultHeaders;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var results = new List<ValidationResult>();

        if (AllowedOrigins is null || AllowedMethods is null || AllowedHeaders is null)
        {
            results.Add(new ValidationResult(
                "Security:Cors:AllowedOrigins, Security:Cors:AllowedMethods and Security:Cors:AllowedHeaders must be lists."));
            return results;
        }

        for (var i = 0; i < AllowedOrigins.Count; i++)
        {
            if (!TryNormalizeOrigin(AllowedOrigins[i], out _))
            {
                results.Add(new ValidationResult(
                    $"Security:Cors:AllowedOrigins:{i} must be an origin such as https://ui.example.test: an http or https scheme and a host, an optional port, no path, no user information, no wildcard."));
            }
        }

        for (var i = 0; i < AllowedMethods.Count; i++)
        {
            var method = AllowedMethods[i]?.Trim();
            if (string.IsNullOrEmpty(method) || !AcceptedMethods.Contains(method, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(new ValidationResult(
                    $"Security:Cors:AllowedMethods:{i} must be one of {string.Join(", ", AcceptedMethods)}."));
            }
        }

        for (var i = 0; i < AllowedHeaders.Count; i++)
        {
            if (!IsHeaderName(AllowedHeaders[i]?.Trim()))
            {
                results.Add(new ValidationResult(
                    $"Security:Cors:AllowedHeaders:{i} must be a header name such as Authorization; the wildcard is not accepted."));
            }
        }

        return results;
    }

    /// <summary>
    /// Normalizes an origin to <c>scheme://host[:port]</c>, lower case, without the default port
    /// and without a trailing slash, which is the form browsers send in the <c>Origin</c> header.
    /// </summary>
    internal static bool TryNormalizeOrigin(string? value, out string origin)
    {
        origin = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        // A wildcard, in any position, is never an exact origin. Uri would accept "*" in a host.
        if (trimmed.Contains('*', StringComparison.Ordinal)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.HostNameType is UriHostNameType.Unknown or UriHostNameType.Basic
            || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/"
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || trimmed.EndsWith('?')
            || trimmed.EndsWith('#'))
        {
            return false;
        }

        // Browsers send the ASCII (punycode) host; an IPv6 host keeps its brackets.
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
        origin = (uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}")
            .ToLowerInvariant();
        return true;
    }

    private static bool IsHeaderName(string? value)
    {
        // RFC 9110 token characters.
        return !string.IsNullOrEmpty(value)
            && value.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal))
            && value != "*";
    }
}
