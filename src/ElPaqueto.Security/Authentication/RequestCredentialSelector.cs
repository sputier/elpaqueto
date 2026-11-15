using ElPaqueto.Security.ApiKeys;
using Microsoft.AspNetCore.Http;

namespace ElPaqueto.Security.Authentication;

/// <summary>Which kind of credential a request carries.</summary>
internal enum CredentialKind
{
    /// <summary>No credential this handler knows: not its business.</summary>
    None,

    /// <summary>A credential that is unusable as sent (malformed Basic header, two API key values): a failure.</summary>
    Invalid,

    /// <summary>An API key, from the <c>X-NuGet-ApiKey</c> header or a Basic password with the token format.</summary>
    ApiKey,

    /// <summary>A user name and a password that does not have the token format.</summary>
    Password,
}

/// <summary>The one credential selected for a request. <see cref="ToString"/> never shows the secret.</summary>
/// <param name="Kind">What was found.</param>
/// <param name="Source">For keys and invalid key headers, where it came from (<see cref="ElPaquetoClaimTypes"/> sources).</param>
/// <param name="UserName">The Basic user name, for passwords.</param>
/// <param name="Secret">The key token or the password.</param>
internal readonly record struct RequestCredential(CredentialKind Kind, string? Source = null, string? UserName = null, string? Secret = null)
{
    public override string ToString() => $"{Kind} ({Source ?? "-"})";
}

/// <summary>
/// Chooses the single credential that decides a request (ADR 0002, decision 6). Purely syntactic: no database, no
/// verification, never throws.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><c>X-NuGet-ApiKey</c> present (even empty): that key, and nothing else. Two values are invalid.</item>
/// <item><c>Authorization: Basic</c>: a password with the exact token format is an API key, any other password is a
/// password. A malformed header is invalid.</item>
/// <item>Anything else: none.</item>
/// </list>
/// </remarks>
internal static class RequestCredentialSelector
{
    /// <summary>The header NuGet clients send the API key in, on push and delete.</summary>
    public const string ApiKeyHeaderName = "X-NuGet-ApiKey";

    public static RequestCredential Select(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var apiKeyValues = headers[ApiKeyHeaderName];
        if (apiKeyValues.Count == 1)
        {
            return new RequestCredential(CredentialKind.ApiKey, ElPaquetoClaimTypes.ApiKeyHeaderSource, Secret: apiKeyValues[0]);
        }

        // A client sends one key; several are ambiguous, hence a failure rather than a pick.
        if (apiKeyValues.Count != 0)
        {
            return new RequestCredential(CredentialKind.Invalid, ElPaquetoClaimTypes.ApiKeyHeaderSource);
        }

        var parsed = BasicCredentialsParser.Parse(headers.Authorization);
        return parsed.Outcome switch
        {
            BasicParseOutcome.Success when ApiKeyToken.IsTokenFormat(parsed.Password)
                => new RequestCredential(CredentialKind.ApiKey, ElPaquetoClaimTypes.BasicPasswordSource, Secret: parsed.Password),
            BasicParseOutcome.Success => new RequestCredential(CredentialKind.Password, UserName: parsed.UserName, Secret: parsed.Password),
            BasicParseOutcome.Malformed => new RequestCredential(CredentialKind.Invalid),
            _ => new RequestCredential(CredentialKind.None),
        };
    }
}
