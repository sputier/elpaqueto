using System;
using System.Linq;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace ElPaqueto.Tests;

/// <summary>
/// A test that needs a real S3-compatible server. It is skipped, with the reason, when no Docker daemon is available
/// and <see cref="DockerAvailability.RequireVariable"/> is not set.
/// </summary>
public sealed class S3FactAttribute : FactAttribute
{
    public S3FactAttribute()
    {
        if (!DockerAvailability.IsRequired && !DockerAvailability.IsAvailable)
        {
            Skip = DockerAvailability.SkipMessage;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class S3Collection : ICollectionFixture<S3Fixture>
{
    public const string Name = "S3";
}

/// <summary>
/// One S3-compatible server in a container for every S3 test of the run; each test gets its own empty bucket.
/// </summary>
/// <remarks>
/// The server is Adobe S3Mock (an HTTP implementation of the S3 API, with path-style access, pagination, multipart
/// and checksums, which the AWS SDK for .NET v4 sends by default). MinIO, the usual choice, no longer publishes
/// container images, so it cannot be pulled reliably.
/// </remarks>
public sealed class S3Fixture : IAsyncLifetime
{
    private const string Image = "adobe/s3mock:5.2.3";
    private const int ContainerPort = 9090;

    // S3Mock accepts any credentials; these are throwaway values, not secrets.
    public const string AccessKey = "test-access-key";
    public const string SecretKey = "test-secret-key";

    private IContainer _container;

    /// <summary>The endpoint of the server, by IP address: the AWS SDK then uses path-style addressing.</summary>
    public Uri Endpoint { get; private set; }

    /// <summary>A client the tests use to arrange and to check what the code under test did.</summary>
    public AmazonS3Client Client { get; private set; }

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            if (DockerAvailability.IsRequired)
            {
                throw new InvalidOperationException(DockerAvailability.SkipMessage);
            }

            return;
        }

        _container = new ContainerBuilder(Image)
            .WithPortBinding(ContainerPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(ContainerPort))
            .Build();
        await _container.StartAsync();

        var host = _container.Hostname == "localhost" ? "127.0.0.1" : _container.Hostname;
        Endpoint = new Uri($"http://{host}:{_container.GetMappedPublicPort(ContainerPort)}");
        Client = new AmazonS3Client(
            new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config { ServiceURL = Endpoint.AbsoluteUri, ForcePathStyle = true });

        // The port answers before the application does.
        Exception last = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                await Client.ListBucketsAsync();
                return;
            }
            catch (Exception e) when (e is AmazonServiceException or AmazonClientException or System.Net.Http.HttpRequestException)
            {
                last = e;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        throw new InvalidOperationException("The S3 server did not answer within a minute.", last);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Creates an empty bucket and returns its name.</summary>
    public async Task<string> CreateBucketAsync()
    {
        var name = "t-" + Guid.NewGuid().ToString("N");
        await Client.PutBucketAsync(name);
        return name;
    }

    /// <summary>Every key of a bucket, read page by page.</summary>
    public async Task<string[]> KeysAsync(string bucket, string prefix = null)
    {
        var keys = new System.Collections.Generic.List<string>();
        string token = null;
        do
        {
            var page = await Client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                ContinuationToken = token,
            });
            keys.AddRange((page.S3Objects ?? []).Select(o => o.Key));
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);

        return [.. keys];
    }
}
