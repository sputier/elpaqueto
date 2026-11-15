using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace ElPaqueto.EndToEnd.Tests.Harness;

/// <summary>
/// Every secret the harness generates (passwords, API keys, Basic header values) is registered here, and
/// everything that reaches the test output goes through <see cref="Redact"/> first.
/// </summary>
internal static partial class Secrets
{
    public const string Mask = "***";

    private static readonly ConcurrentDictionary<string, byte> Known = new(StringComparer.Ordinal);

    /// <summary>Registers a secret and returns it.</summary>
    public static string Register(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        Known.TryAdd(secret, 0);
        return secret;
    }

    /// <summary>Registers a password and the Basic header value built from it.</summary>
    public static void RegisterBasic(string userName, string password)
    {
        Register(password);
        Register(BasicValue(userName, password));
    }

    /// <summary>The base64 part of a Basic <c>Authorization</c> header.</summary>
    public static string BasicValue(string userName, string password)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(userName + ":" + password));

    /// <summary>Replaces every registered secret, and anything shaped like an API key, by <see cref="Mask"/>.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Longest first, so that a secret containing another one is masked whole.
        foreach (var secret in Known.Keys.OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return ApiKeyShape().Replace(text, Mask);
    }

    [GeneratedRegex("epq_[0-9A-Za-z]{12}_[0-9A-Za-z]{43}")]
    private static partial Regex ApiKeyShape();
}
