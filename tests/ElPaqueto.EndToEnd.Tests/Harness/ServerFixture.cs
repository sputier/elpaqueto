using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ElPaqueto.EndToEnd.Tests.Harness;

/// <summary>
/// One server per test class, with fresh data: the published server is started, the first administrator
/// is bootstrapped and the common identities are created through the admin command line. Derived fixtures
/// change settings and seed packages.
/// </summary>
[SuppressMessage("Reliability", "CA1001", Justification = "xUnit disposes fixtures through IAsyncLifetime.DisposeAsync.")]
[SuppressMessage("Performance", "CA1859", Justification = "Scenarios run against the interface, so that a container-backed server can replace the process.")]
public abstract class ServerFixture : IAsyncLifetime
{
    private readonly StringBuilder _setupLog = new();
    private IServerUnderTest? _server;
    private ServerApi? _api;

    internal IServerUnderTest Server => _server ?? throw new InvalidOperationException("The fixture is not initialized.");

    internal AdminCli Admin { get; private set; } = null!;

    internal PackageSet Packages { get; private set; } = null!;

    /// <summary>The bootstrapped administrator (<c>read</c>, <c>publish</c>, <c>admin</c>).</summary>
    internal UserCredential Administrator { get; private set; } = null!;

    /// <summary>A user with <c>read</c> only.</summary>
    internal UserCredential Reader { get; private set; } = null!;

    /// <summary>A user with <c>read</c> and <c>publish</c>.</summary>
    internal UserCredential Publisher { get; private set; } = null!;

    /// <summary>A <c>publish</c> key of <see cref="Publisher"/>.</summary>
    internal ApiKey PublishKey { get; private set; } = null!;

    /// <summary>Server-side checks, as <see cref="Reader"/>.</summary>
    internal ServerApi Api => _api ?? throw new InvalidOperationException("The fixture is not initialized.");

    /// <summary>What the fixture's admin commands printed (redacted).</summary>
    internal string SetupLog
    {
        get
        {
            lock (_setupLog)
            {
                return _setupLog.ToString();
            }
        }
    }

    public async Task InitializeAsync()
    {
        Packages = await TestPackages.GetAsync();
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        Configure(settings);
        _server = new ServerProcess(await ServerBuild.GetAsync(), settings);
        try
        {
            await _server.StartAsync();

            Admin = new AdminCli(_server, LogSetup);
            Administrator = await Admin.BootstrapAsync("admin");
            Reader = await Admin.CreateUserAsync("reader", "reader");
            Publisher = await Admin.CreateUserAsync("publisher", "publisher");
            PublishKey = await Admin.CreateKeyAsync("publisher", "push", "publish");
            _api = new ServerApi(_server, Reader);
            await SeedAsync();
        }
        catch
        {
            // Do not rely on the framework disposing a fixture whose initialization failed.
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        _api?.Dispose();
        _api = null;
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }

    /// <summary>A new isolated client whose <c>elpaqueto</c> source carries the given credentials (none: no credentials).</summary>
    internal async Task<NuGetClient> CreateClientAsync(ITestOutputHelper output, UserCredential? sourceCredentials)
    {
        var client = new NuGetClient(output);
        try
        {
            await client.LogToolVersionsAsync();
            (await client.AddSourceAsync(Server.ServiceIndex, sourceCredentials)).ShouldSucceed();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Writes what the server logged since <paramref name="logOffset"/>, redacted, to the test output.</summary>
    internal void WriteServerLog(ITestOutputHelper output, int logOffset)
    {
        var log = Server.Log;
        output.WriteLine("--- server log during the test");
        output.WriteLine(Secrets.Redact(log[Math.Min(logOffset, log.Length)..]));
    }

    /// <summary>Server settings, as environment variable names (<c>Section__Key</c>); null removes a default.</summary>
    protected virtual void Configure(IDictionary<string, string?> settings)
    {
    }

    /// <summary>Server-side state the tests of the class start from.</summary>
    protected virtual Task SeedAsync() => Task.CompletedTask;

    private void LogSetup(string text)
    {
        lock (_setupLog)
        {
            _setupLog.AppendLine(text);
        }
    }
}

/// <summary>Base class of the scenarios: the fixture, the test output and the server log of each test.</summary>
public abstract class EndToEndTest<TFixture> : IDisposable
    where TFixture : ServerFixture
{
    private readonly int _logOffset;
    private readonly List<NuGetClient> _clients = [];

    protected EndToEndTest(TFixture fixture, ITestOutputHelper output)
    {
        Fixture = fixture;
        Output = output;
        _logOffset = fixture.Server.Log.Length;
    }

    protected TFixture Fixture { get; }

    protected ITestOutputHelper Output { get; }

    public void Dispose()
    {
        try
        {
            Fixture.WriteServerLog(Output, _logOffset);
        }
        catch (InvalidOperationException)
        {
            // The output is closed when the test was cancelled.
        }

        foreach (var client in _clients)
        {
            client.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A client disposed with the test.</summary>
    internal async Task<NuGetClient> ClientAsync(UserCredential? sourceCredentials)
    {
        var client = await Fixture.CreateClientAsync(Output, sourceCredentials);
        _clients.Add(client);
        return client;
    }

    /// <summary>A client without any source, disposed with the test.</summary>
    internal NuGetClient BareClient()
    {
        var client = new NuGetClient(Output);
        _clients.Add(client);
        return client;
    }

    /// <summary>A unique name for users and keys created by one test.</summary>
    protected static string UniqueName(string prefix)
        => prefix + "-" + System.Security.Cryptography.RandomNumberGenerator.GetHexString(6, lowercase: true);

    /// <summary>The admin commands of the fixture's server, logging to this test's output.</summary>
    internal AdminCli Admin => new(Fixture.Server, text => Output.WriteLine(text));
}
