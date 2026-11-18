using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using ElPaqueto.Gcp;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Options;
using Xunit;

namespace ElPaqueto.Tests;

/// <summary>
/// A test that needs the fake GCS server container. It is skipped, with the reason, when no Docker daemon is available
/// and <see cref="DockerAvailability.RequireVariable"/> is not set.
/// </summary>
public sealed class GcsFactAttribute : FactAttribute
{
    public GcsFactAttribute()
    {
        if (!DockerAvailability.IsRequired && !DockerAvailability.IsAvailable)
        {
            Skip = DockerAvailability.SkipMessage;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class GcsCollection : ICollectionFixture<GcsFixture>
{
    public const string Name = "Gcs";
}

/// <summary>
/// One <c>fsouza/fake-gcs-server</c> in a container for every Google Cloud Storage test of the run; each test gets its
/// own empty bucket. The service under test reaches it through <see cref="EmulatorStorageService"/>, which overrides the
/// one client factory method of <c>GoogleCloudStorageService</c>.
/// </summary>
public sealed class GcsFixture : IAsyncLifetime
{
    // Pinned: a fake of the GCS JSON API, not a Google image.
    private const string Image = "fsouza/fake-gcs-server:1.52.2";

    private IContainer _container;
    private string _baseUri;

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

        // The server puts its own public address in the resumable upload session URLs it returns, so the port the
        // client reaches must be known before it starts: the container listens on the same port as the host.
        // The free port can be taken between FreePort() and the container start: try once more with another port.
        for (var attempt = 1; ; attempt++)
        {
            var port = FreePort();
            _container = new ContainerBuilder(Image)
                .WithPortBinding(port, port)
                .WithCommand("-scheme", "http", "-port", port.ToString(CultureInfo.InvariantCulture),
                    "-external-url", $"http://127.0.0.1:{port}", "-backend", "memory")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort((ushort)port).ForPath("/storage/v1/b")))
                .Build();
            try
            {
                await _container.StartAsync();
                _baseUri = $"http://127.0.0.1:{port}/storage/v1/";
                return;
            }
            catch (Exception) when (attempt < 2)
            {
                await _container.DisposeAsync();
                _container = null;
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>A client for the fake server: no credentials, no Google endpoint.</summary>
    public async Task<StorageClient> CreateClientAsync() =>
        await new StorageClientBuilder { BaseUri = _baseUri, UnauthenticatedAccess = true }.BuildAsync();

    /// <summary>Creates an empty bucket and returns its name.</summary>
    public async Task<string> CreateBucketAsync()
    {
        var name = "t-" + Guid.NewGuid().ToString("N");
        using var client = await CreateClientAsync();
        await client.CreateBucketAsync("elpaqueto-test", name);
        return name;
    }

    /// <summary>The production service with its client pointed at the fake server.</summary>
    public sealed class EmulatorStorageService(IOptionsSnapshot<GoogleCloudStorageOptions> options, GcsFixture fixture)
        : GoogleCloudStorageService(options)
    {
        protected override Task<StorageClient> CreateClientAsync() => fixture.CreateClientAsync();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
