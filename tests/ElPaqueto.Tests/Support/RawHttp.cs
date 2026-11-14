using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ElPaqueto.Tests;

/// <summary>
/// Sends an HTTP/1.1 request byte for byte over a socket: no client library normalizes the method, the request
/// target or the headers, so the server sees exactly what an attacker would send.
/// </summary>
/// <remarks>
/// The response is read until the server closes the connection (<c>Connection: close</c>), within
/// <see cref="Timeout"/>. Chunked bodies are not decoded: the suites only read short bodies, or check that a body
/// is empty.
/// </remarks>
internal static class RawHttp
{
    /// <summary>How long one exchange may take before the test fails instead of hanging.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The answer: the status code, the headers (names in lower case) and the body as text.</summary>
    public sealed record Response(int Status, IReadOnlyDictionary<string, string> Headers, string Body)
    {
        public string Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var value) ? value : null;
    }

    public static async Task<Response> SendAsync(
        Uri server,
        string method,
        string target,
        IEnumerable<KeyValuePair<string, string>> headers = null,
        byte[] body = null)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(server.Host, server.Port, timeout.Token);
        await using var stream = tcp.GetStream();

        var head = new StringBuilder()
            .Append(method).Append(' ').Append(target).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(server.Authority).Append("\r\n")
            .Append("Connection: close\r\n");
        foreach (var (name, value) in headers ?? [])
        {
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        body ??= method is "POST" or "PUT" or "PATCH" ? [] : null;
        if (body is not null)
        {
            head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        }

        head.Append("\r\n");
        var text = head.ToString();
        if (text.Any(c => c > 0x7F))
        {
            // Encoding.ASCII would silently send '?' instead: the request would not be the one the test means.
            throw new ArgumentException("A raw request must be ASCII; percent-encode anything else: " + target, nameof(target));
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), timeout.Token);
        if (body is { Length: > 0 })
        {
            await stream.WriteAsync(body, timeout.Token);
        }

        using var received = new MemoryStream();
        await stream.CopyToAsync(received, timeout.Token);
        return Parse(Encoding.UTF8.GetString(received.ToArray()));
    }

    private static Response Parse(string raw)
    {
        var end = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidDataException("Incomplete HTTP response: " + raw);
        }

        var lines = raw[..end].Split("\r\n");
        var status = int.Parse(lines[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        var headers = lines.Skip(1)
            .Select(line => line.Split(':', 2))
            .GroupBy(parts => parts[0].Trim().ToLowerInvariant())
            .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(parts => parts[1].Trim())));

        return new Response(status, headers, raw[(end + 4)..]);
    }
}
