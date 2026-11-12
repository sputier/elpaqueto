using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ElPaqueto.Security.Passwords;

/// <summary>
/// Spends the time of a number of password-hashing iterations. Used to make an account whose stored
/// hash is cheaper than the configured cost take as long as any other.
/// </summary>
internal interface IPasswordTimingPad
{
    public void Pad(int iterations);
}

internal sealed class Pbkdf2TimingPad : IPasswordTimingPad
{
    private static readonly byte[] Password = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] Salt = RandomNumberGenerator.GetBytes(16);

    public void Pad(int iterations)
    {
        if (iterations <= 0)
        {
            return;
        }

        _ = Rfc2898DeriveBytes.Pbkdf2(Password, Salt, iterations, HashAlgorithmName.SHA512, 32);
    }
}

/// <summary>
/// Reads the iteration count that ASP.NET Core Identity's V3 format records in the hash itself.
/// </summary>
internal static class PasswordHashFormat
{
    private const byte V3Marker = 0x01;
    private const int HeaderLength = 13;

    /// <summary>Returns the iteration count of a V3 hash, or <see langword="false"/> for anything else.</summary>
    public static bool TryGetIterations(string hash, out int iterations)
    {
        iterations = 0;

        // A V3 hash is 61 bytes: a bounded length keeps this cheap for any stored value.
        if (string.IsNullOrEmpty(hash) || hash.Length > 512)
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(hash);
            if (bytes.Length < HeaderLength || bytes[0] != V3Marker)
            {
                return false;
            }

            var value = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5, 4));
            if (value is 0 or > int.MaxValue)
            {
                return false;
            }

            iterations = (int)value;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
