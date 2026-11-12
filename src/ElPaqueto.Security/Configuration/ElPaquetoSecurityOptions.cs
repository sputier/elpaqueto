using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

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
    /// Trusted reverse proxies. Section: <c>Security:ReverseProxy</c>.
    /// </summary>
    public SecurityReverseProxyOptions ReverseProxy { get; set; } = new();

    /// <summary>
    /// Transport policy. Section: <c>Security:Transport</c>.
    /// </summary>
    public SecurityTransportOptions Transport { get; set; } = new();

    /// <summary>
    /// The public URL clients use to reach the server, such as <c>https://nuget.example.test</c>.
    /// Setting: <c>Security:PublicBaseUrl</c>. Optional: an empty value means not set.
    /// </summary>
    /// <remarks>
    /// When set, every absolute URL the server generates uses its scheme and host, whatever the
    /// request's <c>Host</c> or <c>X-Forwarded-Host</c> header says. It must be an absolute
    /// <c>https</c> URL without path, query, fragment or user information: a path prefix is set
    /// with <c>PathBase</c>.
    /// </remarks>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// The parsed <see cref="PublicBaseUrl"/>, or <see langword="null"/> when it is not set or invalid.
    /// </summary>
    internal Uri? PublicBaseUri => TryParsePublicBaseUrl(PublicBaseUrl, out var uri) ? uri : null;

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

        foreach (var result in ValidateSection(ReverseProxy, nameof(ReverseProxy)))
        {
            yield return result;
        }

        foreach (var result in ValidateSection(Transport, nameof(Transport)))
        {
            yield return result;
        }

        if (!string.IsNullOrWhiteSpace(PublicBaseUrl) && !TryParsePublicBaseUrl(PublicBaseUrl, out _))
        {
            yield return new ValidationResult(
                "Security:PublicBaseUrl must be an absolute https URL without path, query, fragment or user information, such as https://nuget.example.test.");
        }
    }

    private static bool TryParsePublicBaseUrl(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;

        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || parsed.HostNameType is UriHostNameType.Unknown or UriHostNameType.Basic
            || parsed.UserInfo.Length != 0
            || parsed.AbsolutePath != "/"
            || parsed.Query.Length != 0
            || parsed.Fragment.Length != 0
            || value.Trim().EndsWith('?')
            || value.Trim().EndsWith('#'))
        {
            return false;
        }

        uri = parsed;
        return true;
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
