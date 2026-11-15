using System.Security.Cryptography;

namespace ElPaqueto.Security.ApiKeys;

/// <summary>
/// Compares a computed secret hash with a stored one. A seam so that tests can prove every
/// validation goes through the constant-time comparison.
/// </summary>
internal interface IApiKeyHashComparer
{
    public bool HashesEqual(ReadOnlySpan<byte> computed, ReadOnlySpan<byte> stored);
}

/// <summary>
/// Constant-time comparison: the time taken does not depend on how many leading bytes match.
/// </summary>
internal sealed class FixedTimeApiKeyHashComparer : IApiKeyHashComparer
{
    public bool HashesEqual(ReadOnlySpan<byte> computed, ReadOnlySpan<byte> stored)
        => CryptographicOperations.FixedTimeEquals(computed, stored);
}
