using System.Net;
using System.Text.Json;
using ElPaqueto.EndToEnd.Tests.Harness;

namespace ElPaqueto.EndToEnd.Tests.Security;

/// <summary>What a crawled URL is for, which decides how it is replayed.</summary>
internal enum CrawledKind
{
    /// <summary>A resource read with GET: <c>200</c> with <c>Read</c>.</summary>
    Read,

    /// <summary>A base address the protocol appends paths to (registrations, package content): not a resource itself.</summary>
    Prefix,

    /// <summary>A write endpoint (package and symbol publish): replayed with PUT.</summary>
    Write,
}

/// <summary>A URL found by the crawl, and where it was found.</summary>
internal sealed record CrawledUrl(Uri Url, CrawledKind Kind, string FoundIn);

/// <summary>
/// Collects every URL the server advertises, starting from the service index, with a <c>Read</c> user:
/// the resources of the index, the URLs the V3 protocol derives from them (registration index, flat-container
/// versions, <c>.nupkg</c>, <c>.nuspec</c>, readme, search and autocomplete queries), and, recursively, every
/// absolute URL found in a JSON response. URLs of another origin are recorded, never followed.
/// </summary>
internal static class ServiceIndexCrawler
{
    public static async Task<IReadOnlyList<CrawledUrl>> CrawlAsync(ServerApi api, Uri serviceIndex, IReadOnlyList<TestPackage> packages)
    {
        var found = new Dictionary<string, CrawledUrl>(StringComparer.Ordinal);
        var queue = new Queue<CrawledUrl>();

        void Add(Uri url, CrawledKind kind, string foundIn)
        {
            if (found.TryAdd(url.AbsoluteUri, new CrawledUrl(url, kind, foundIn)) && kind == CrawledKind.Read && SameOrigin(url, serviceIndex))
            {
                queue.Enqueue(found[url.AbsoluteUri]);
            }
        }

        using (var index = await GetJsonAsync(api, serviceIndex))
        {
            Add(serviceIndex, CrawledKind.Read, "the configured source");
            foreach (var resource in index.RootElement.GetProperty("resources").EnumerateArray())
            {
                var url = new Uri(resource.GetProperty("@id").GetString()!);
                var type = resource.GetProperty("@type").GetString()!;
                var foundIn = "index.json " + type;
                var kind = type.Split('/')[0] switch
                {
                    "PackagePublish" or "SymbolPackagePublish" => CrawledKind.Write,
                    "RegistrationsBaseUrl" or "PackageBaseAddress" => CrawledKind.Prefix,
                    _ => CrawledKind.Read,
                };
                Add(url, kind, foundIn);

                // The URLs a client builds from each resource, as the V3 protocol defines them.
                var prefix = url.AbsoluteUri;
                foreach (var package in packages)
                {
                    var id = package.Id.ToLowerInvariant();
                    var version = package.Version.ToLowerInvariant();
                    switch (type.Split('/')[0])
                    {
                        case "RegistrationsBaseUrl":
                            Add(new Uri($"{prefix}{id}/index.json"), CrawledKind.Read, foundIn + " (registration index)");
                            break;
                        case "PackageBaseAddress":
                            Add(new Uri($"{prefix}{id}/index.json"), CrawledKind.Read, foundIn + " (versions)");
                            Add(new Uri($"{prefix}{id}/{version}/{id}.{version}.nupkg"), CrawledKind.Read, foundIn + " (.nupkg)");
                            Add(new Uri($"{prefix}{id}/{version}/{id}.nuspec"), CrawledKind.Read, foundIn + " (.nuspec)");
                            Add(new Uri($"{prefix}{id}/{version}/readme"), CrawledKind.Read, foundIn + " (readme)");
                            break;
                        case "SearchQueryService":
                            Add(new Uri($"{prefix}?q={id}&prerelease=true&semVerLevel=2.0.0"), CrawledKind.Read, foundIn + " (query)");
                            break;
                        case "SearchAutocompleteService":
                            Add(new Uri($"{prefix}?q={id}&prerelease=true&semVerLevel=2.0.0"), CrawledKind.Read, foundIn + " (ids)");
                            Add(new Uri($"{prefix}?id={id}&prerelease=true&semVerLevel=2.0.0"), CrawledKind.Read, foundIn + " (versions)");
                            break;
                    }
                }
            }
        }

        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            using var response = await api.SendAsync(ServerApi.Request(HttpMethod.Get, next.Url, api.Reader));
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {next.Url} ({next.FoundIn}) with Read answered {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            {
                continue;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var url in AbsoluteUrls(json.RootElement))
            {
                Add(url, CrawledKind.Read, "a response of " + next.Url.AbsolutePath);
            }
        }

        return found.Values.ToList();
    }

    public static bool SameOrigin(Uri url, Uri reference)
        => url.Scheme == reference.Scheme && url.Host == reference.Host && url.Port == reference.Port;

    private static async Task<JsonDocument> GetJsonAsync(ServerApi api, Uri url)
    {
        using var response = await api.SendAsync(ServerApi.Request(HttpMethod.Get, url, api.Reader));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    // Every string value that is an absolute http(s) URL, except JSON-LD identifiers: the vocabulary of "@context"
    // and the types of "@type" (such as http://schema.nuget.org/catalog#Permalink) name concepts, not resources.
    private static IEnumerable<Uri> AbsoluteUrls(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject().Where(p => p.Name is not "@context" and not "@type"))
                {
                    foreach (var url in AbsoluteUrls(property.Value))
                    {
                        yield return url;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var url in AbsoluteUrls(item))
                    {
                        yield return url;
                    }
                }

                break;
            case JsonValueKind.String:
                if (Uri.TryCreate(element.GetString(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    yield return uri;
                }

                break;
        }
    }
}
