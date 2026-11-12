using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ElPaqueto.Security.Data;

namespace ElPaqueto.Security.Throttling;

/// <summary>
/// The client a failure is counted against: an IPv4 address, or the /64 network of an IPv6 address.
/// </summary>
/// <remarks>
/// One IPv6 subscriber usually receives a whole /64, so counting single IPv6 addresses would let one client
/// rotate through billions of them. IPv4 addresses seen through a dual-stack socket (<c>::ffff:a.b.c.d</c>)
/// are counted as the IPv4 address, never grouped. A request without an address (a Unix socket) is counted
/// in one shared bucket.
/// </remarks>
internal readonly record struct ClientKey(UInt128 Value)
{
    private const int IPv6PrefixLength = 64;

    public static ClientKey From(IPAddress? address)
    {
        if (address is null)
        {
            return default;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        Span<byte> bytes = stackalloc byte[16];
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // Kept apart from every IPv6 key: the top bits mark the family.
            address.TryWriteBytes(bytes[12..], out _);
            bytes[10] = 0xFF;
            bytes[11] = 0xFF;
            return new ClientKey(BinaryPrimitives.ReadUInt128BigEndian(bytes));
        }

        address.TryWriteBytes(bytes, out _);
        var value = BinaryPrimitives.ReadUInt128BigEndian(bytes);
        return new ClientKey(value & (UInt128.MaxValue << (128 - IPv6PrefixLength)));
    }

    /// <summary>A form safe to write to logs: the IPv4 address, or the IPv6 network in CIDR notation.</summary>
    public static string Describe(IPAddress? address)
    {
        if (address is null)
        {
            return "(no address)";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes).ToString() + "/" + IPv6PrefixLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The user name a failure is counted against, known or not: a fixed-size digest of the normalized name,
/// so that a long or random user name costs the same small amount of memory as any other.
/// </summary>
internal readonly record struct AccountKey(UInt128 Value)
{
    public static AccountKey From(string userName)
    {
        var normalized = User.Normalize(userName);
        var maxBytes = Encoding.UTF8.GetMaxByteCount(normalized.Length);
        var buffer = maxBytes <= 512 ? stackalloc byte[maxBytes] : new byte[maxBytes];
        var length = Encoding.UTF8.GetBytes(normalized, buffer);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(buffer[..length], digest);
        return new AccountKey(BinaryPrimitives.ReadUInt128BigEndian(digest));
    }
}

/// <summary>A user name tried from a client.</summary>
internal readonly record struct PairKey(ClientKey Client, AccountKey Account);
