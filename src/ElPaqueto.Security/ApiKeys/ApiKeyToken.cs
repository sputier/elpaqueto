using System.Text.RegularExpressions;

namespace ElPaqueto.Security.ApiKeys;

/// <summary>
/// The API key token format: <c>epq_&lt;keyId&gt;_&lt;secret&gt;</c>, where the key id is 12 and the
/// secret 43 base62 characters (<c>0-9A-Za-z</c>), 60 characters in total.
/// </summary>
/// <remarks>
/// Base62 contains neither <c>_</c> nor <c>-</c>, so the token splits unambiguously on <c>_</c>, is
/// selected whole by a double-click, and is safe in HTTP headers, XML (<c>NuGet.Config</c>) and Basic
/// credentials. Parsing is purely syntactic and never touches the database.
/// </remarks>
public static partial class ApiKeyToken
{
    /// <summary>The fixed prefix that makes a leaked key recognizable.</summary>
    public const string Prefix = "epq_";

    /// <summary>Length of the public key identifier.</summary>
    public const int KeyIdLength = 12;

    /// <summary>Length of the secret: 43 base62 characters carry log2(62) × 43 ≈ 256.03 bits.</summary>
    public const int SecretLength = 43;

    /// <summary>Total length of a token.</summary>
    public const int Length = 4 + KeyIdLength + 1 + SecretLength;

    /// <summary>
    /// Inputs longer than this are rejected before any other processing, whatever they contain.
    /// </summary>
    public const int MaximumInputLength = 128;

    /// <summary>
    /// The complete token pattern, anchored on the whole input (<c>\z</c>, not <c>$</c>, which would
    /// also accept a trailing line feed). Suitable for secret scanning without the anchors.
    /// </summary>
    public const string Pattern = @"\Aepq_[0-9A-Za-z]{12}_[0-9A-Za-z]{43}\z";

    /// <summary>
    /// Returns whether <paramref name="value"/> has the exact token format. Used to route a Basic
    /// password to API key validation and to reject passwords that look like a key.
    /// </summary>
    /// <param name="value">The candidate value.</param>
    /// <returns><see langword="true"/> when the value is a well-formed token.</returns>
    public static bool IsTokenFormat(string? value) => TryParse(value, out _);

    /// <summary>
    /// Splits a token into its key id and secret.
    /// </summary>
    /// <param name="value">The candidate token, as received.</param>
    /// <param name="token">The parts, when the value is well-formed.</param>
    /// <returns>
    /// <see langword="true"/> when the value is a well-formed token. No normalization is applied:
    /// whitespace, another case for the prefix or any other deviation makes the value malformed.
    /// </returns>
    public static bool TryParse(string? value, out ApiKeyTokenParts token)
    {
        token = default;

        if (value is null || value.Length != Length || !TokenRegex().IsMatch(value))
        {
            return false;
        }

        token = new ApiKeyTokenParts(
            value.Substring(Prefix.Length, KeyIdLength),
            value.Substring(Prefix.Length + KeyIdLength + 1, SecretLength));
        return true;
    }

    /// <summary>Builds the token from its parts.</summary>
    internal static string Format(string keyId, string secret) => string.Concat(Prefix, keyId, "_", secret);

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TokenRegex();
}
