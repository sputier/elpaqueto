using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ElPaqueto.Tests;

/// <summary>
/// What a client can observe of a response, and clients that show every response as the server sent it.
/// </summary>
internal static class ResponseSnapshot
{
    /// <summary>
    /// The status, every header except <c>Date</c> (sorted by name) and the body, as one string: two responses a
    /// client cannot tell apart have the same fingerprint.
    /// </summary>
    public static async Task<string> FingerprintAsync(HttpResponseMessage response)
    {
        var headers = response.Headers
            .Concat(response.Content.Headers)
            .Where(h => !h.Key.Equals("Date", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => $"{h.Key}: {string.Join(",", h.Value)}");

        return $"{(int)response.StatusCode}|{string.Join("|", headers)}|{Convert.ToBase64String(await response.Content.ReadAsByteArrayAsync())}";
    }

    /// <summary>
    /// A client that sends no credential and never follows a redirect, so that a 3xx or a <c>Location</c> header
    /// reaches the test instead of the page it points to.
    /// </summary>
    public static HttpClient CreateClientWithoutRedirects(this WebApplicationFactory<Startup> app)
        => app.CreateAnonymousClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    /// <summary>Sends a request with an empty body for the methods that carry one.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, Action<HttpRequestMessage> credentials = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "PUT" or "POST")
        {
            request.Content = new ByteArrayContent([]);
        }

        credentials?.Invoke(request);
        return await client.SendAsync(request);
    }
}
