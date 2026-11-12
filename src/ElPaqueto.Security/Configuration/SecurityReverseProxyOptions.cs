using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;

namespace ElPaqueto.Security.Configuration;

/// <summary>
/// The reverse proxies whose forwarded headers are trusted (<c>Security:ReverseProxy</c>).
/// </summary>
/// <remarks>
/// <c>X-Forwarded-For</c>, <c>X-Forwarded-Proto</c> and, without a public base URL,
/// <c>X-Forwarded-Host</c> are applied only when the connection comes from one of these addresses.
/// When neither list is set, only loopback is trusted (<c>127.0.0.0/8</c> and <c>::1</c>); setting
/// either list replaces that default. There is no way to trust every address.
/// </remarks>
public sealed class SecurityReverseProxyOptions : IValidatableObject
{
    /// <summary>The highest accepted <see cref="ForwardLimit"/>.</summary>
    public const int MaximumForwardLimit = 10;

    private const int IPv4MappedPrefixLength = 96;

    private static readonly byte[] IPv4MappedRange = IPAddress.Parse("::ffff:0:0").GetAddressBytes();

    /// <summary>
    /// IP addresses of trusted proxies. Setting: <c>Security:ReverseProxy:KnownProxies</c>.
    /// </summary>
    public IList<string> KnownProxies { get; set; } = [];

    /// <summary>
    /// Networks of trusted proxies in CIDR notation, such as <c>172.18.0.0/16</c>.
    /// Setting: <c>Security:ReverseProxy:KnownNetworks</c>.
    /// </summary>
    public IList<string> KnownNetworks { get; set; } = [];

    /// <summary>
    /// How many entries of <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> are processed, from
    /// the right. One per trusted proxy in the chain. Setting: <c>Security:ReverseProxy:ForwardLimit</c>.
    /// </summary>
    [Range(1, MaximumForwardLimit, ErrorMessage = "Security:ReverseProxy:ForwardLimit must be between 1 and 10.")]
    public int ForwardLimit { get; set; } = 1;

    /// <summary>
    /// Whether the configuration replaces the loopback-only default.
    /// </summary>
    internal bool HasExplicitProxies => (KnownProxies?.Count ?? 0) + (KnownNetworks?.Count ?? 0) > 0;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var results = new List<ValidationResult>();

        if (KnownProxies is null || KnownNetworks is null)
        {
            results.Add(new ValidationResult("Security:ReverseProxy:KnownProxies and Security:ReverseProxy:KnownNetworks must be lists."));
            return results;
        }

        for (var i = 0; i < KnownProxies.Count; i++)
        {
            if (!TryParseProxy(KnownProxies[i], out _))
            {
                results.Add(new ValidationResult(
                    $"Security:ReverseProxy:KnownProxies:{i} is not a valid IP address; the unspecified address (0.0.0.0, ::) is not accepted."));
            }
        }

        for (var i = 0; i < KnownNetworks.Count; i++)
        {
            var value = KnownNetworks[i];

            if (!TryParseNetwork(value, out var network))
            {
                results.Add(new ValidationResult(
                    $"Security:ReverseProxy:KnownNetworks:{i} is not a valid CIDR network such as 172.18.0.0/16 (the address must not have bits set beyond the prefix)."));
            }
            else if (IsCatchAll(network))
            {
                results.Add(new ValidationResult(
                    $"Security:ReverseProxy:KnownNetworks:{i} trusts every address: list the networks of your proxies instead."));
            }
        }

        return results;
    }

    /// <summary>
    /// Whether a network covers every IPv4 or every IPv6 address. An IPv6 network that contains the
    /// whole IPv4-mapped range (<c>::ffff:0:0/96</c>) also trusts every IPv4 client of a dual-stack socket.
    /// </summary>
    internal static bool IsCatchAll(IPNetwork network)
    {
        if (network.PrefixLength == 0)
        {
            return true;
        }

        // Compared bit by bit: IPNetwork.Contains does not give reliable answers for IPv4-mapped addresses.
        return network.BaseAddress.AddressFamily == AddressFamily.InterNetworkV6
            && network.PrefixLength <= IPv4MappedPrefixLength
            && SharesPrefix(network.BaseAddress.GetAddressBytes(), IPv4MappedRange, network.PrefixLength);
    }

    private static bool SharesPrefix(byte[] left, byte[] right, int prefixLength)
    {
        for (var bit = 0; bit < prefixLength; bit++)
        {
            var mask = (byte)(0x80 >> (bit % 8));
            if ((left[bit / 8] & mask) != (right[bit / 8] & mask))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses a proxy address. Requires the dotted or colon notation: <see cref="IPAddress.TryParse(string, out IPAddress)"/>
    /// alone would also accept forms such as <c>1</c> (<c>0.0.0.1</c>).
    /// </summary>
    internal static bool TryParseProxy(string? value, out IPAddress address)
    {
        address = IPAddress.None;

        if (string.IsNullOrWhiteSpace(value)
            || !(value.Contains('.', StringComparison.Ordinal) || value.Contains(':', StringComparison.Ordinal))
            || !IPAddress.TryParse(value.Trim(), out var parsed))
        {
            return false;
        }

        if (parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        address = parsed;
        return true;
    }

    /// <summary>
    /// Parses a network in CIDR notation; the prefix length is mandatory.
    /// </summary>
    internal static bool TryParseNetwork(string? value, out IPNetwork network)
    {
        network = default;

        if (string.IsNullOrWhiteSpace(value) || !value.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        var trimmed = value.Trim();
        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        var address = trimmed[..slash];

        // Same rule as for proxies: no shorthand such as "10/8".
        if (!(address.Contains('.', StringComparison.Ordinal) || address.Contains(':', StringComparison.Ordinal)))
        {
            return false;
        }

        // IPNetwork.TryParse silently clears host bits ("10.0.0.1/8" becomes 10.0.0.0/8): a value
        // with host bits set is most likely a typo, so it is rejected instead.
        if (!IPNetwork.TryParse(trimmed, out var parsed)
            || !IPAddress.TryParse(address, out var written)
            || !written.Equals(parsed.BaseAddress))
        {
            return false;
        }

        if (parsed.BaseAddress.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return false;
        }

        network = parsed;
        return true;
    }
}
