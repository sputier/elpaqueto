using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ElPaqueto.Core;
using ElPaqueto.Gcp;
using Google;
using Google.Cloud.Storage.V1;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

/// <summary>
/// The Google Cloud Storage provider against <c>fsouza/fake-gcs-server</c> started by Testcontainers: the storage
/// service itself, then the real host with <c>Storage:Type=GoogleCloud</c>. They are skipped, with the reason, when no
/// Docker daemon is available; the CI job sets <c>ELPAQUETO_REQUIRE_DOCKER=1</c>. See docs/technical/testing.md.
/// </summary>
[Collection(GcsCollection.Name)]
[Trait("Category", "Gcs")]
public sealed class GoogleCloudStorageIntegrationTests(GcsFixture gcs, ITestOutputHelper output)
{
    private const string ObjectPath = "packages/acme.gcs/1.0.0/acme.gcs.1.0.0.nupkg";

    private sealed class Snapshot(GoogleCloudStorageOptions value) : IOptionsSnapshot<GoogleCloudStorageOptions>
    {
        public GoogleCloudStorageOptions Value { get; } = value;

        public GoogleCloudStorageOptions Get(string name) => Value;
    }

    private GoogleCloudStorageService Service(string bucket) =>
        new GcsFixture.EmulatorStorageService(new Snapshot(new GoogleCloudStorageOptions { BucketName = bucket }), gcs);

    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static async Task<byte[]> ReadAsync(Stream stream)
    {
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private static MultipartFormDataContent Multipart(byte[] package)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(package), "package", "package.nupkg");
        return content;
    }

    [GcsFact]
    public async Task PutThenGet_ReturnsTheSameBytes_AndNormalizesBackslashes()
    {
        var service = Service(await gcs.CreateBucketAsync());
        var content = Bytes(300_000);

        var result = await service.PutAsync("acme.core\\1.0.0\\acme.core.1.0.0.nupkg", new MemoryStream(content), "binary/octet-stream");

        Assert.Equal(StoragePutResult.Success, result);
        await using var stream = await service.GetAsync("acme.core/1.0.0/acme.core.1.0.0.nupkg");
        Assert.Equal(content, await ReadAsync(stream));
    }

    [GcsFact]
    public async Task Put_SameContentTwice_IsAlreadyExists_DifferentContent_IsConflict()
    {
        var service = Service(await gcs.CreateBucketAsync());
        var content = Bytes(1024);

        Assert.Equal(StoragePutResult.Success, await service.PutAsync("a/b.bin", new MemoryStream(content), "binary/octet-stream"));
        Assert.Equal(StoragePutResult.AlreadyExists, await service.PutAsync("a/b.bin", new MemoryStream(content), "binary/octet-stream"));
        Assert.Equal(StoragePutResult.Conflict, await service.PutAsync("a/b.bin", new MemoryStream(Bytes(1024)), "binary/octet-stream"));

        // the first content was not overwritten
        await using var stream = await service.GetAsync("a/b.bin");
        Assert.Equal(content, await ReadAsync(stream));
    }

    [GcsFact]
    public async Task Get_MissingObject_ThrowsGoogleApiExceptionNotFound()
    {
        var service = Service(await gcs.CreateBucketAsync());

        var e = await Assert.ThrowsAsync<GoogleApiException>(() => service.GetAsync("does/not/exist.bin"));

        Assert.Equal(HttpStatusCode.NotFound, e.HttpStatusCode);
    }

    [GcsFact]
    public async Task Delete_RemovesTheObject_AndIgnoresAMissingOne()
    {
        var bucket = await gcs.CreateBucketAsync();
        var service = Service(bucket);
        await service.PutAsync("x/y.bin", new MemoryStream(Bytes(10)), "binary/octet-stream");

        await service.DeleteAsync("x/y.bin");
        await service.DeleteAsync("x/y.bin");
        await service.DeleteAsync("never/existed.bin");

        using var client = await gcs.CreateClientAsync();
        var e = await Assert.ThrowsAsync<GoogleApiException>(() => client.GetObjectAsync(bucket, "x/y.bin"));
        Assert.Equal(HttpStatusCode.NotFound, e.HttpStatusCode);
    }

    [Fact]
    public async Task GetDownloadUri_PointsToTheObjectInTheBucket()
    {
        var uri = await Service("my-bucket").GetDownloadUriAsync("a\\b/c.nupkg");

        Assert.Equal("https://storage.googleapis.com/my-bucket/a/b/c.nupkg", uri.ToString());
    }

    [GcsFact]
    public async Task Host_PushThenDownload_ThroughTheGoogleCloudStorageType()
    {
        var bucket = await gcs.CreateBucketAsync();
        await using var baseApp = new ElPaquetoApplication(output, inMemoryConfiguration: dict =>
        {
            dict["Storage:Type"] = "GoogleCloud";
            dict["Storage:BucketName"] = bucket;
        });
        // Only the client of the real GoogleCloudStorageService is replaced (it reaches the fake server).
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddTransient<GoogleCloudStorageService>(provider => new GcsFixture.EmulatorStorageService(
                provider.GetRequiredService<IOptionsSnapshot<GoogleCloudStorageOptions>>(), gcs))));
        using var client = app.CreateClient();
        var package = TestPackageBuilder.Create("Acme.Gcs", "1.0.0");

        using (var push = await client.PutAsync("api/v2/package", Multipart(package)))
        {
            Assert.Equal(HttpStatusCode.Created, push.StatusCode);
        }

        // the package really is in the bucket
        using var storage = await gcs.CreateClientAsync();
        using var stored = new MemoryStream();
        await storage.DownloadObjectAsync(bucket, ObjectPath, stored);
        Assert.Equal(package, stored.ToArray());

        Assert.Equal(package, await client.GetByteArrayAsync("v3/package/acme.gcs/1.0.0/acme.gcs.1.0.0.nupkg"));

        using (var duplicate = await client.PutAsync("api/v2/package", Multipart(package)))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }

        using (var delete = await client.DeleteAsync("api/v2/package/Acme.Gcs/1.0.0"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        var e = await Assert.ThrowsAsync<GoogleApiException>(() => storage.GetObjectAsync(bucket, ObjectPath));
        Assert.Equal(HttpStatusCode.NotFound, e.HttpStatusCode);
    }
}
