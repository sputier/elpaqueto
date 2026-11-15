namespace ElPaqueto.EndToEnd.Tests.Harness;

/// <summary>How a server was stopped.</summary>
internal enum StopKind
{
    /// <summary>SIGTERM, then a clean exit with code 0.</summary>
    Graceful,

    /// <summary>The process tree was killed.</summary>
    Killed,
}

/// <summary>
/// A running ElPaqueto server, reached over plain HTTP on loopback. <see cref="ServerProcess"/> runs the
/// published server as a child process; a container-backed implementation can reuse the same scenarios.
/// </summary>
internal interface IServerUnderTest : IAsyncDisposable
{
    /// <summary>The root URL, ending with a slash: <c>http://127.0.0.1:&lt;port&gt;/</c>.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The V3 service index.</summary>
    public Uri ServiceIndex { get; }

    /// <summary>The package storage directory (<c>Storage:Path</c>) as seen from the tests.</summary>
    public string StorageDirectory { get; }

    /// <summary>Whether <see cref="StopAsync"/> can stop the server gracefully on this platform.</summary>
    public bool SupportsGracefulStop { get; }

    /// <summary>Everything the server wrote so far, unredacted.</summary>
    public string Log { get; }

    /// <summary>Whether the server process is running.</summary>
    public bool IsRunning { get; }

    /// <summary>Starts the server (again, on the same port and data after a stop) and waits until it is live.</summary>
    public Task StartAsync();

    /// <summary>Stops the server gracefully where supported, otherwise kills it, and says which happened.</summary>
    public Task<StopKind> StopAsync();

    /// <summary>Kills the server's process tree, as a crash or a power loss would.</summary>
    public Task KillAsync();

    /// <summary>Runs <c>admin ...</c> with the server's configuration and data.</summary>
    public Task<CommandResult> RunAdminAsync(IReadOnlyList<string> arguments, string? standardInput = null);
}
