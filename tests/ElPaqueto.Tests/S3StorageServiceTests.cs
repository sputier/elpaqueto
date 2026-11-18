using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Web;
using Amazon.Runtime;
using Amazon.S3;
using ElPaqueto.Aws;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ElPaqueto.Tests;

/// <summary>
/// What of <see cref="S3StorageService"/> can be checked without a server: pre-signing is local, so these tests run
/// everywhere (the container tests of the same service are in <see cref="S3IntegrationTests"/>).
/// </summary>
public sealed class S3StorageServiceTests
{
    private static S3StorageService CreateService(string bucket, string prefix, string endpoint)
    {
        var options = new S3StorageOptions { Bucket = bucket, Prefix = prefix };
        var snapshot = new Mock<IOptionsSnapshot<S3StorageOptions>>();
        snapshot.Setup(s => s.Value).Returns(options);

        var client = new AmazonS3Client(
            new BasicAWSCredentials("test-access-key", "test-secret-key"),
            new AmazonS3Config { ServiceURL = endpoint });

        return new S3StorageService(snapshot.Object, client);
    }

    [Fact]
    public async Task GetDownloadUriAsync_ReturnsASignedUrlThatExpires_WithTheKeyUnderThePrefix()
    {
        // The AWS SDK for .NET requires an expiry on a pre-signed URL.
        var service = CreateService("my-bucket", "packages", "http://127.0.0.1:9000");

        var uri = await service.GetDownloadUriAsync("acme.core\\1.0.0\\acme.core.1.0.0.nupkg");

        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal("/my-bucket/packages/acme.core/1.0.0/acme.core.1.0.0.nupkg", uri.AbsolutePath);
        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("AWS4-HMAC-SHA256", query["X-Amz-Algorithm"]);
        Assert.False(string.IsNullOrEmpty(query["X-Amz-Signature"]));
        var expires = int.Parse(query["X-Amz-Expires"], CultureInfo.InvariantCulture);
        Assert.InRange(expires, 1, 7 * 24 * 3600);
    }
}
