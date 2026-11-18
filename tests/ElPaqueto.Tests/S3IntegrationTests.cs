using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ElPaqueto.Aws;
using ElPaqueto.Core;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// <see cref="S3StorageService"/> and the real host configured with <c>Storage:Type=AwsS3</c>, against a real
/// S3-compatible server (Adobe S3Mock) started by Testcontainers, with the AWS SDK for .NET v4. Each test works in its
/// own empty bucket of one shared container. The tests are skipped, with the reason, when no Docker daemon is
/// available; the CI job sets <c>ELPAQUETO_REQUIRE_DOCKER=1</c> so that they cannot be skipped there. See
/// docs/technical/testing.md.
/// </summary>
[Collection(S3Collection.Name)]
[Trait("Category", "S3")]
public sealed class S3IntegrationTests(S3Fixture s3, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly List<ElPaquetoApplication> _apps = [];
    private readonly List<AmazonS3Client> _clients = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.DisposeAsync();
        }

        foreach (var client in _clients)
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// The service the way the application configures it for an endpoint: a service URL and credentials, nothing else
    /// (no path-style switch, the endpoint is an IP address).
    /// </summary>
    private S3StorageService CreateService(string bucket, string prefix = null)
    {
        var snapshot = new Mock<IOptionsSnapshot<S3StorageOptions>>();
        snapshot.Setup(o => o.Value).Returns(new S3StorageOptions { Bucket = bucket, Prefix = prefix });

        var client = new AmazonS3Client(
            new BasicAWSCredentials(S3Fixture.AccessKey, S3Fixture.SecretKey),
            new AmazonS3Config { ServiceURL = s3.Endpoint.AbsoluteUri });
        _clients.Add(client);

        return new S3StorageService(snapshot.Object, client);
    }

    private ElPaquetoApplication StartHost(string bucket, string prefix = null, Action<Dictionary<string, string>> configure = null)
    {
        var app = new ElPaquetoApplication(
            output,
            inMemoryConfiguration: dict =>
            {
                dict["Storage:Type"] = "AwsS3";
                dict["Storage:Endpoint"] = s3.Endpoint.AbsoluteUri;
                dict["Storage:Bucket"] = bucket;
                dict["Storage:AccessKey"] = S3Fixture.AccessKey;
                dict["Storage:SecretKey"] = S3Fixture.SecretKey;
                if (prefix is not null)
                {
                    dict["Storage:Prefix"] = prefix;
                }

                configure?.Invoke(dict);
            },
            keepHealthChecks: true);
        _apps.Add(app);
        return app;
    }

    private static byte[] RandomBytes(int length) => RandomNumberGenerator.GetBytes(length);

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static async Task<HttpResponseMessage> PushAsync(HttpClient client, byte[] package)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(package), "package", "package.nupkg");
        return await client.PutAsync("api/v2/package", content);
    }

    // The storage service.

    [S3Fact]
    public async Task PutThenGet_RoundTripsTheBytes_AndStoresTheContentType()
    {
        var bucket = await s3.CreateBucketAsync();
        var service = CreateService(bucket);
        // Several megabytes: more than the copy buffers of the service, and a size that is not a multiple of them.
        var content = RandomBytes((5 * 1024 * 1024) + 123);

        using var input = new MemoryStream(content);
        var result = await service.PutAsync("data/file.bin", input, "application/octet-stream");

        Assert.Equal(StoragePutResult.Success, result);
        await using var read = await service.GetAsync("data/file.bin");
        Assert.Equal(content, await ReadAllAsync(read));
        var metadata = await s3.Client.GetObjectMetadataAsync(bucket, "data/file.bin");
        Assert.Equal("application/octet-stream", metadata.Headers.ContentType);
        Assert.Equal(content.Length, metadata.ContentLength);
    }

    [S3Fact]
    public async Task Put_OfAnEmptyContent_StoresAnEmptyObject()
    {
        var bucket = await s3.CreateBucketAsync();
        var service = CreateService(bucket);

        using var input = new MemoryStream();
        await service.PutAsync("empty.bin", input, "application/octet-stream");

        await using var read = await service.GetAsync("empty.bin");
        Assert.Empty(await ReadAllAsync(read));
        Assert.Equal(0, (await s3.Client.GetObjectMetadataAsync(bucket, "empty.bin")).ContentLength);
    }

    [S3Fact]
    public async Task Put_OnAnExistingPath_ReplacesTheContent()
    {
        var bucket = await s3.CreateBucketAsync();
        var service = CreateService(bucket);

        using (var first = new MemoryStream([1, 2, 3]))
        {
            await service.PutAsync("same.bin", first, "application/octet-stream");
        }

        using (var second = new MemoryStream([9, 8, 7, 6]))
        {
            Assert.Equal(StoragePutResult.Success, await service.PutAsync("same.bin", second, "application/octet-stream"));
        }

        await using var read = await service.GetAsync("same.bin");
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, await ReadAllAsync(read));
    }

    [S3Fact]
    public async Task Paths_UseForwardSlashes_AndTheConfiguredPrefix()
    {
        var bucket = await s3.CreateBucketAsync();
        var service = CreateService(bucket, prefix: "tenant-a");

        using (var input = new MemoryStream([1]))
        {
            // The package storage builds its paths with the separator of the operating system.
            await service.PutAsync("packages\\acme.core\\1.0.0\\acme.core.1.0.0.nupkg", input, "application/octet-stream");
        }

        // The prefix gets its trailing slash; the backslashes are converted.
        Assert.Equal(["tenant-a/packages/acme.core/1.0.0/acme.core.1.0.0.nupkg"], await s3.KeysAsync(bucket));
        await using var read = await service.GetAsync("packages/acme.core/1.0.0/acme.core.1.0.0.nupkg");
        Assert.Equal(new byte[] { 1 }, await ReadAllAsync(read));
    }

    [S3Fact]
    public async Task Get_OfAMissingObject_ThrowsNoSuchKey_WithoutLeavingTheBucketChanged()
    {
        var bucket = await s3.CreateBucketAsync();
        var service = CreateService(bucket);

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => service.GetAsync("missing.bin"));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal("NoSuchKey", error.ErrorCode);
        Assert.Empty(await s3.KeysAsync(bucket));
    }

    [S3Fact]
    public async Task Get_OnAMissingBucket_Throws()
    {
        var service = CreateService("t-" + Guid.NewGuid().ToString("N"));

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => service.GetAsync("any.bin"));

        Assert.Equal("NoSuchBucket", error.ErrorCode);
    }

    [S3Fact]
    public async Task Delete_RemovesOnlyTheObject_AndIsIdempotent()
    {
        var bucket = await s3.CreateBucketAsync();
        var service = CreateService(bucket);
        foreach (var path in new[] { "a/one.bin", "a/two.bin" })
        {
            using var input = new MemoryStream([1]);
            await service.PutAsync(path, input, "application/octet-stream");
        }

        await service.DeleteAsync("a/one.bin");
        await service.DeleteAsync("a/one.bin");
        await service.DeleteAsync("never/existed.bin");

        Assert.Equal(["a/two.bin"], await s3.KeysAsync(bucket));
    }

    [S3Fact]
    public async Task GetDownloadUriAsync_ReturnsAUrlThatTheServerAccepts()
    {
        var bucket = await s3.CreateBucketAsync();
        var service = CreateService(bucket, prefix: "p");
        var content = RandomBytes(10_000);
        using (var input = new MemoryStream(content))
        {
            await service.PutAsync("dir/file.bin", input, "application/octet-stream");
        }

        var uri = await service.GetDownloadUriAsync("dir/file.bin");

        // The server checks the signature and the expiry.
        using var http = new HttpClient();
        Assert.Equal(content, await http.GetByteArrayAsync(uri));
    }

    [S3Fact]
    public async Task Put_WithACancelledToken_IsCancelled_AndStoresNothing()
    {
        var bucket = await s3.CreateBucketAsync();
        var service = CreateService(bucket);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        using var input = new MemoryStream([1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PutAsync("c.bin", input, "application/octet-stream", cts.Token));

        Assert.Empty(await s3.KeysAsync(bucket));
    }

    // The real host.

    [S3Fact]
    public async Task Host_PushThenDownload_ThroughTheBucket_AndHardDeleteRemovesTheObjects()
    {
        var bucket = await s3.CreateBucketAsync();
        var app = StartHost(bucket, prefix: "feed", configure: dict => dict["PackageDeletionBehavior"] = "HardDelete");
        using var client = app.CreateClient();
        var package = TestPackageBuilder.Create("Acme.Core", "1.0.0");

        using (var health = await client.GetAsync("health"))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        using (var pushed = await PushAsync(client, package))
        {
            Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);
        }

        var keys = await s3.KeysAsync(bucket);
        Assert.Contains("feed/packages/acme.core/1.0.0/acme.core.1.0.0.nupkg", keys);
        Assert.Contains("feed/packages/acme.core/1.0.0/acme.core.1.0.0.nuspec", keys);
        Assert.All(keys, key => Assert.StartsWith("feed/", key, StringComparison.Ordinal));

        Assert.Equal(package, await client.GetByteArrayAsync("v3/package/acme.core/1.0.0/acme.core.1.0.0.nupkg"));
        using (var nuspec = await client.GetAsync("v3/package/acme.core/1.0.0/acme.core.nuspec"))
        {
            Assert.Equal(HttpStatusCode.OK, nuspec.StatusCode);
            Assert.Contains("<id>Acme.Core</id>", await nuspec.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var duplicate = await PushAsync(client, package))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }

        using (var deleted = await client.DeleteAsync("api/v2/package/Acme.Core/1.0.0"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        Assert.Empty(await s3.KeysAsync(bucket));
        using (var gone = await client.GetAsync("v3/package/acme.core/1.0.0/acme.core.1.0.0.nupkg"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        using var again = await PushAsync(client, package);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [S3Fact]
    public async Task Host_WithSeveralPackages_ServesEachOfThem_FromTheSameBucket()
    {
        var bucket = await s3.CreateBucketAsync();
        var app = StartHost(bucket);
        using var client = app.CreateClient();
        var packages = Enumerable.Range(1, 12)
            .ToDictionary(i => $"Acme.Pkg{i}", i => TestPackageBuilder.Create($"Acme.Pkg{i}", $"{i}.0.0"));

        foreach (var (id, bytes) in packages)
        {
            using var pushed = await PushAsync(client, bytes);
            Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);
        }

        Assert.Equal(packages.Count * 2, (await s3.KeysAsync(bucket)).Length);
        foreach (var (id, bytes) in packages)
        {
            var version = $"{id["Acme.Pkg".Length..]}.0.0";
            var lower = id.ToLowerInvariant();
            Assert.Equal(bytes, await client.GetByteArrayAsync($"v3/package/{lower}/{version}/{lower}.{version}.nupkg"));
        }
    }
}
