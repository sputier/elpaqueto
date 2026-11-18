using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Google.Cloud.Storage.V1;
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
/// own empty bucket. The production code is not given a seam: <c>StorageClient.CreateAsync()</c> honours the
/// <c>STORAGE_EMULATOR_HOST</c> environment variable (emulator address, no credentials), which this fixture sets for the
/// process while it lives. That is why the tests of this collection never run in parallel with each other.
/// </summary>
public sealed class GcsFixture : IAsyncLifetime
{
    // Pinned: a fake of the GCS JSON API, not a Google image.
    private const string Image = "fsouza/fake-gcs-server:1.52.2";
    private const string EmulatorHostVariable = "STORAGE_EMULATOR_HOST";

    private IContainer _container;
    private string _previousEmulatorHost;

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
        var port = FreePort();
        _container = new ContainerBuilder(Image)
            .WithPortBinding(port, port)
            .WithCommand("-scheme", "http", "-port", port.ToString(CultureInfo.InvariantCulture),
                "-external-url", $"http://127.0.0.1:{port}", "-backend", "memory")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort((ushort)port).ForPath("/storage/v1/b")))
            .Build();
        await _container.StartAsync();

        _previousEmulatorHost = Environment.GetEnvironmentVariable(EmulatorHostVariable);
        Environment.SetEnvironmentVariable(EmulatorHostVariable, $"http://127.0.0.1:{port}");
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(EmulatorHostVariable, _previousEmulatorHost);
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>A client built the way the production code builds it (ADC / emulator variable).</summary>
    public async Task<StorageClient> CreateClientAsync() => await StorageClient.CreateAsync();

    /// <summary>Creates an empty bucket and returns its name.</summary>
    public async Task<string> CreateBucketAsync()
    {
        var name = "t-" + Guid.NewGuid().ToString("N");
        using var client = await CreateClientAsync();
        await client.CreateBucketAsync("elpaqueto-test", name);
        return name;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
