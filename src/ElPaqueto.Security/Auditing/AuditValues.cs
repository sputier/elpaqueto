using System.Net;
using System.Security.Cryptography;
using System.Text;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Authentication;

namespace ElPaqueto.Security.Auditing;

/// <summary>
/// Turns request data into values that are safe to write to the audit trail: only allow-listed text and
/// fixed vocabularies reach a log record, never a credential.
/// </summary>
internal static class AuditValues
{
    /// <summary>The value logged where a field does not apply, or is unknown.</summary>
    public const string None = "-";

    /// <summary>The <c>Method</c> of a password sign-in.</summary>
    public const string PasswordMethod = ElPaquetoClaimTypes.PasswordMethod;

    /// <summary>The <c>Method</c> of an API key sign-in.</summary>
    public const string ApiKeyMethod = ElPaquetoClaimTypes.ApiKeyMethod;

    private const string InvalidUserNamePrefix = "<invalid:";
    private const string UnknownUserNamePrefix = "<unknown:";

    // Per process: a fingerprint correlates the attempts of one run without being a stable identifier of the
    // value, and nobody can precompute the fingerprint of a guessed password.
    private static readonly byte[] FingerprintKey = RandomNumberGenerator.GetBytes(32);

    private static readonly string[] KnownHttpMethods =
        ["GET", "HEAD", "POST", "PUT", "DELETE", "PATCH", "OPTIONS"];

    /// <summary>
    /// The user name of an existing account as it may be logged: itself when it follows the user name rules (ASCII
    /// allow-list, at most 64 characters) and does not look like an API key, otherwise <c>&lt;invalid:xxxxxxxx&gt;</c>,
    /// a keyed fingerprint of the value.
    /// </summary>
    /// <param name="userName">A user name read from the security database.</param>
    /// <returns>A value without control characters, spaces or anything outside the allow-list.</returns>
    public static string UserName(string? userName)
    {
        if (string.IsNullOrEmpty(userName))
        {
            return None;
        }

        // An API key token is made of allowed characters: a name containing its prefix is never logged as is.
        if (UserNames.IsValid(userName) && !userName.Contains(ApiKeyToken.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return userName;
        }

        return InvalidUserNamePrefix + Fingerprint(userName) + ">";
    }

    /// <summary>
    /// A user name sent by a client that matches no account: always <c>&lt;unknown:xxxxxxxx&gt;</c>, a keyed
    /// fingerprint, never the value.
    /// </summary>
    /// <param name="userName">The user name as sent.</param>
    /// <returns>The fingerprint, or <see cref="None"/> when there is no user name.</returns>
    /// <remarks>
    /// A value that matches no account may be a password or an API key typed in the user name field (both can be
    /// made of allowed characters), or carry line breaks that would forge log lines. The fingerprint (the first
    /// 4 bytes of an HMAC-SHA256 under a key drawn at process start) still lets an operator see the same value
    /// being retried.
    /// </remarks>
    public static string UnknownUserName(string? userName)
        => string.IsNullOrEmpty(userName) ? None : UnknownUserNamePrefix + Fingerprint(userName) + ">";

    private static string Fingerprint(string value)
    {
        var mac = HMACSHA256.HashData(FingerprintKey, Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(mac, 0, 4);
    }

    /// <summary>The client address after trusted forwarded-header processing, or <see cref="None"/>.</summary>
    /// <param name="address">The connection's remote address.</param>
    /// <returns>The textual address.</returns>
    public static string Client(IPAddress? address)
    {
        if (address is null)
        {
            return None;
        }

        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
    }

    /// <summary>The request method if it is a standard one, otherwise <c>OTHER</c>.</summary>
    /// <param name="method">The request method.</param>
    /// <returns>An upper-case method name from a fixed list.</returns>
    public static string HttpMethod(string? method)
    {
        foreach (var known in KnownHttpMethods)
        {
            if (string.Equals(known, method, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return "OTHER";
    }

    /// <summary>The scheme of a refused plain-HTTP request: <c>http</c>, or <c>other</c>.</summary>
    /// <param name="scheme">The request scheme after forwarded-header processing.</param>
    /// <returns>A value from a fixed list.</returns>
    public static string Scheme(string? scheme)
        => string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase) ? "http" : "other";

    /// <summary>The reason of an API key refusal, in the snake_case vocabulary of the audit trail.</summary>
    /// <param name="failure">The validation failure.</param>
    /// <returns>A value from a fixed list.</returns>
    public static string Reason(ApiKeyValidationFailure failure) => failure switch
    {
        ApiKeyValidationFailure.Oversized => "oversized",
        ApiKeyValidationFailure.Malformed => "malformed",
        ApiKeyValidationFailure.UnknownKey => "unknown_key",
        ApiKeyValidationFailure.WrongSecret => "wrong_secret",
        ApiKeyValidationFailure.Revoked => "revoked",
        ApiKeyValidationFailure.Expired => "expired",
        ApiKeyValidationFailure.OwnerDisabled => "owner_disabled",
        _ => "other",
    };
}

/// <summary>The <c>Reason</c> values of <see cref="AuditLog.AuthenticationFailed"/>.</summary>
internal static class AuthenticationFailureReason
{
    /// <summary>The <c>Authorization: Basic</c> header cannot be decoded into a user name and a password.</summary>
    public const string Malformed = "malformed";

    /// <summary>No user has this name (an invalid user name included).</summary>
    public const string UnknownUser = "unknown_user";

    /// <summary>The user exists and is active, but the password is wrong.</summary>
    public const string BadPassword = "bad_password";

    /// <summary>The user exists but is disabled.</summary>
    public const string Disabled = "disabled";
}

/// <summary>The <c>Reason</c> values of <see cref="AuditLog.AuthorizationDenied"/>.</summary>
internal static class AuthorizationDenialReason
{
    /// <summary>The endpoint has no row in the permission table.</summary>
    public const string UnmappedEndpoint = "unmapped_endpoint";

    /// <summary>The endpoint reuses the route name of a row for another action.</summary>
    public const string RouteMismatch = "route_mismatch";

    /// <summary>The row does not allow the request method.</summary>
    public const string MethodNotAllowed = "method_not_allowed";

    /// <summary>A write was attempted without an API key sent in <c>X-NuGet-ApiKey</c>.</summary>
    public const string ApiKeyHeaderRequired = "api_key_header_required";

    /// <summary>The caller lacks the permission the endpoint requires.</summary>
    public const string MissingPermission = "missing_permission";
}
