using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ElPaqueto.EndToEnd.Tests.Harness;

/// <summary>
/// Raw HTTP against the running server: reads of the server-side state (with a <c>Read</c> user), and the
/// setup steps no <c>dotnet</c> command expresses. Scenarios assert with the real client; this class checks
/// what the server did.
/// </summary>
internal sealed class ServerApi : IDisposable
{
    private readonly IServerUnderTest _server;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(60) };

    public ServerApi(IServerUnderTest server, UserCredential reader)
    {
        _server = server;
        Reader = reader;
    }

    public UserCredential Reader { get; }

    /// <summary>A request with Basic credentials, an API key in <c>X-NuGet-ApiKey</c>, both or none.</summary>
    public static HttpRequestMessage Request(HttpMethod method, Uri uri, UserCredential? basic = null, ApiKey? apiKey = null)
    {
        var request = new HttpRequestMessage(method, uri);
        if (basic is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Secrets.BasicValue(basic.UserName, basic.Password));
        }

        if (apiKey is not null)
        {
            request.Headers.Add("X-NuGet-ApiKey", apiKey.Token);
        }

        return request;
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => _client.SendAsync(request);

    public Uri Url(string relative) => new(_server.BaseAddress, relative);

    /// <summary>The registration index of a package, or null when the server does not know it.</summary>
    public async Task<JsonDocument?> GetRegistrationAsync(string id)
    {
        using var response = await SendAsync(Request(HttpMethod.Get, Url($"v3/registration/{id.ToLowerInvariant()}/index.json"), Reader));
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Every version in the registration, with its <c>listed</c> flag and download count.</summary>
    public async Task<IReadOnlyDictionary<string, (bool Listed, long Downloads)>> GetVersionsAsync(string id)
    {
        using var registration = await GetRegistrationAsync(id);
        var versions = new Dictionary<string, (bool, long)>(StringComparer.OrdinalIgnoreCase);
        if (registration is null)
        {
            return versions;
        }

        foreach (var page in registration.RootElement.GetProperty("items").EnumerateArray())
        {
            foreach (var leaf in page.GetProperty("items").EnumerateArray())
            {
                var entry = leaf.GetProperty("catalogEntry");
                versions[entry.GetProperty("version").GetString()!] = (entry.GetProperty("listed").GetBoolean(), entry.GetProperty("downloads").GetInt64());
            }
        }

        return versions;
    }

    public async Task<bool> HasVersionAsync(TestPackage package)
        => (await GetVersionsAsync(package.Id)).ContainsKey(package.Version);

    /// <summary>The <c>listed</c> flag of the registration leaf of a version.</summary>
    public async Task<bool> IsListedAsync(TestPackage package)
    {
        var versions = await GetVersionsAsync(package.Id);
        Assert.True(versions.TryGetValue(package.Version, out var version), $"{package} is not registered.");
        return version.Listed;
    }

    public async Task<long> DownloadsAsync(TestPackage package)
    {
        var versions = await GetVersionsAsync(package.Id);
        Assert.True(versions.TryGetValue(package.Version, out var version), $"{package} is not registered.");
        return version.Downloads;
    }

    /// <summary>The flat-container URL of a package's <c>.nupkg</c>.</summary>
    public Uri PackageContentUrl(string id, string version)
    {
        var lowerId = id.ToLowerInvariant();
        var lowerVersion = version.ToLowerInvariant();
        return Url($"v3/package/{lowerId}/{lowerVersion}/{lowerId}.{lowerVersion}.nupkg");
    }

    /// <summary>Downloads a <c>.nupkg</c> as the reader: its bytes, or null on 404.</summary>
    public async Task<byte[]?> DownloadAsync(TestPackage package)
    {
        using var response = await SendAsync(Request(HttpMethod.Get, PackageContentUrl(package.Id, package.Version), Reader));
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Pushes a package as a setup step (multipart, as NuGet does), accepting an existing version.</summary>
    public async Task EnsurePushedAsync(ApiKey publishKey, params TestPackage[] packages)
    {
        foreach (var package in packages)
        {
            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(await File.ReadAllBytesAsync(package.Path));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(file, "package", Path.GetFileName(package.Path));
            using var request = Request(HttpMethod.Put, Url("api/v2/package"), apiKey: publishKey);
            request.Content = content;
            using var response = await SendAsync(request);
            Assert.True(
                response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
                $"Setup push of {package} answered {(int)response.StatusCode}.");
        }
    }

    /// <summary>Unlists a version (the default deletion behaviour) as a setup step.</summary>
    public async Task UnlistAsync(ApiKey publishKey, TestPackage package)
    {
        using var response = await SendAsync(Request(HttpMethod.Delete, Url($"api/v2/package/{package.Id}/{package.Version}"), apiKey: publishKey));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>Relists a version: <c>POST api/v2/package/{id}/{version}</c>. No dotnet command does it.</summary>
    public async Task<HttpStatusCode> RelistAsync(ApiKey publishKey, TestPackage package)
    {
        using var response = await SendAsync(Request(HttpMethod.Post, Url($"api/v2/package/{package.Id}/{package.Version}"), apiKey: publishKey));
        return response.StatusCode;
    }

    /// <summary>The path of a version's <c>.nupkg</c> in the file system storage.</summary>
    public string StoredPackagePath(TestPackage package)
    {
        var lowerId = package.Id.ToLowerInvariant();
        var lowerVersion = package.Version.ToLowerInvariant();
        return Path.Combine(_server.StorageDirectory, "packages", lowerId, lowerVersion, $"{lowerId}.{lowerVersion}.nupkg");
    }

    /// <summary>The SHA-512 of a stored file, in hexadecimal.</summary>
    public static string Sha512(byte[] bytes) => Convert.ToHexString(SHA512.HashData(bytes));

    /// <summary>The SHA-512 of every file of the storage, by relative path.</summary>
    public IReadOnlyDictionary<string, string> StorageHashes()
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(_server.StorageDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(_server.StorageDirectory, "*", SearchOption.AllDirectories))
            {
                hashes[Path.GetRelativePath(_server.StorageDirectory, file).Replace('\\', '/')] = Sha512(File.ReadAllBytes(file));
            }
        }

        return hashes;
    }

    public void Dispose() => _client.Dispose();
}
