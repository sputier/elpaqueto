using System.Security.Cryptography;
using System.Text;

namespace ElPaqueto.Security.ApiKeys;

/// <summary>
/// The stored form of a secret: SHA-256 of its ASCII bytes.
/// </summary>
/// <remarks>
/// A fast hash without salt is sufficient because the secret is 256 bits of uniform randomness: a
/// stolen hash cannot be brute-forced, and precomputed tables are useless against such a space. A
/// slow password hash would only make every request expensive.
/// </remarks>
internal static class ApiKeySecretHash
{
    /// <summary>Hashes a secret of exactly <see cref="ApiKeyToken.SecretLength"/> base62 characters.</summary>
    /// <param name="secret">The secret part of a parsed or generated token.</param>
    /// <returns>The 32-byte hash.</returns>
    public static byte[] Compute(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (secret.Length != ApiKeyToken.SecretLength)
        {
            // Never reached with a parsed token; the message does not repeat the value.
            throw new ArgumentException("The secret does not have the expected length.", nameof(secret));
        }

        Span<byte> bytes = stackalloc byte[ApiKeyToken.SecretLength];
        Encoding.ASCII.GetBytes(secret, bytes);
        var hash = SHA256.HashData(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return hash;
    }
}
