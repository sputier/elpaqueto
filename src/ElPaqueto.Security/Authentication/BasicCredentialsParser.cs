using System.Text;
using Microsoft.Extensions.Primitives;

namespace ElPaqueto.Security.Authentication;

/// <summary>What the parser concluded about an <c>Authorization</c> header.</summary>
internal enum BasicParseOutcome
{
    /// <summary>No header, or another authentication scheme: not this handler's business.</summary>
    NoResult,

    /// <summary>A Basic header that is malformed in any way.</summary>
    Malformed,

    /// <summary>A well-formed Basic header.</summary>
    Success,
}

internal readonly record struct BasicParseResult(BasicParseOutcome Outcome, string? UserName, string? Password)
{
    public static BasicParseResult NoResult { get; } = new(BasicParseOutcome.NoResult, null, null);

    public static BasicParseResult Malformed { get; } = new(BasicParseOutcome.Malformed, null, null);
}

/// <summary>
/// Strict parser for HTTP Basic credentials (RFC 7617 over RFC 9110).
/// </summary>
/// <remarks>
/// It does not use <c>AuthenticationHeaderValue.Parse</c>, which accepts any scheme. Anything that
/// is not exactly <c>Basic 1*SP base64(UTF-8 "user:password")</c> is malformed. It never throws.
/// </remarks>
internal static class BasicCredentialsParser
{
    /// <summary>Longest accepted header value: a 64-character user name and a 256-character password fit comfortably.</summary>
    public const int MaxHeaderLength = 2048;

    private const string Scheme = "Basic";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static BasicParseResult Parse(StringValues headerValues)
    {
        if (headerValues.Count == 0)
        {
            return BasicParseResult.NoResult;
        }

        // A client sends one credential; several are ambiguous, hence a failure rather than a pick.
        if (headerValues.Count > 1)
        {
            return BasicParseResult.Malformed;
        }

        var value = headerValues[0];
        if (string.IsNullOrEmpty(value))
        {
            return BasicParseResult.NoResult;
        }

        if (value.Length > MaxHeaderLength)
        {
            return BasicParseResult.Malformed;
        }

        var span = value.AsSpan();
        if (span.Length < Scheme.Length || !span[..Scheme.Length].Equals(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return BasicParseResult.NoResult;
        }

        var afterScheme = span[Scheme.Length..];
        if (afterScheme.Length == 0)
        {
            return BasicParseResult.Malformed;
        }

        // "Basicfoo" is another scheme, not a Basic header without a separator.
        if (afterScheme[0] != ' ')
        {
            return BasicParseResult.NoResult;
        }

        var token = afterScheme.TrimStart(' ');
        if (token.IsEmpty || !IsBase64Alphabet(token))
        {
            return BasicParseResult.Malformed;
        }

        var buffer = new byte[MaxHeaderLength];
        if (!Convert.TryFromBase64Chars(token, buffer, out var written))
        {
            return BasicParseResult.Malformed;
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(buffer, 0, written);
        }
        catch (DecoderFallbackException)
        {
            return BasicParseResult.Malformed;
        }

        // Split on the first colon only: a password may contain colons, a user name may not.
        var separator = decoded.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return BasicParseResult.Malformed;
        }

        return new BasicParseResult(BasicParseOutcome.Success, decoded[..separator], decoded[(separator + 1)..]);
    }

    private static bool IsBase64Alphabet(ReadOnlySpan<char> token)
    {
        foreach (var c in token)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))
            {
                return false;
            }
        }

        return true;
    }
}
