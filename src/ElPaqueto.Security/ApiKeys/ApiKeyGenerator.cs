using System.Security.Cryptography;
using System.Text;

namespace ElPaqueto.Security.ApiKeys;

/// <summary>
/// Generates key ids and secrets: uniformly distributed base62 characters drawn from a
/// cryptographically secure random number generator.
/// </summary>
/// <remarks>
/// Each random byte is mapped to a character by rejection sampling: bytes 0 to 247 (4 × 62 values)
/// map to <c>byte % 62</c>, so every character has exactly four antecedents; bytes 248 to 255 are
/// discarded. Taking <c>byte % 62</c> of every byte would make the first eight characters more likely.
/// </remarks>
internal sealed class ApiKeyGenerator
{
    /// <summary>The 62 characters, in the order of their value.</summary>
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>Bytes at or above this value are rejected (the largest multiple of 62 that fits in a byte).</summary>
    public const int RejectionThreshold = 248;

    private readonly Action<Span<byte>> _fill;

    public ApiKeyGenerator()
        : this(RandomNumberGenerator.Fill)
    {
    }

    /// <summary>Test seam: the source of random bytes.</summary>
    internal ApiKeyGenerator(Action<Span<byte>> fill)
    {
        _fill = fill;
    }

    public static ApiKeyGenerator Default { get; } = new();

    /// <summary>Maps one random byte to a character, or rejects it.</summary>
    /// <param name="value">The random byte.</param>
    /// <param name="character">The character, when the byte is accepted.</param>
    /// <returns>Whether the byte is accepted.</returns>
    public static bool TryMap(byte value, out char character)
    {
        if (value >= RejectionThreshold)
        {
            character = default;
            return false;
        }

        character = Alphabet[value % Alphabet.Length];
        return true;
    }

    public string GenerateKeyId() => Generate(ApiKeyToken.KeyIdLength);

    public string GenerateSecret() => Generate(ApiKeyToken.SecretLength);

    /// <summary>Generates a complete token and returns its parts.</summary>
    /// <returns>The key id and the secret.</returns>
    public (string KeyId, string Secret) GenerateToken() => (GenerateKeyId(), GenerateSecret());

    private string Generate(int length)
    {
        var builder = new StringBuilder(length);

        // 8 of 256 byte values are rejected: one draw is almost always enough.
        Span<byte> buffer = stackalloc byte[length + 16];

        while (builder.Length < length)
        {
            _fill(buffer);

            foreach (var value in buffer)
            {
                if (builder.Length == length)
                {
                    break;
                }

                if (TryMap(value, out var character))
                {
                    builder.Append(character);
                }
            }
        }

        CryptographicOperations.ZeroMemory(buffer);
        return builder.ToString();
    }
}
