using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ElPaqueto.EndToEnd.Tests.Harness;

namespace ElPaqueto.EndToEnd.Tests;

/// <summary>The harness itself (#36): the smoke scenario, isolation, redaction, timeouts and cleanup.</summary>
[Trait("Category", "EndToEnd")]
public sealed class HarnessTests : EndToEndTest<HarnessTests.Setup>, IClassFixture<HarnessTests.Setup>
{
    public HarnessTests(Setup fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Smoke_PushWithAPublishKey_ThenRestoreWithClearedCaches()
    {
        var packages = Fixture.Packages;
        var client = await ClientAsync(Fixture.Reader);

        (await client.PushAsync(packages.Base100, Fixture.PublishKey)).ShouldSucceed().ShouldContain("Your package was pushed.");
        Assert.True(await Fixture.Api.HasVersionAsync(packages.Base100));

        var project = client.WriteProject("consumer", [(PackageSet.BaseId, "1.0.0")]);
        await client.ClearCachesAsync();
        (await client.DotnetAsync(project, ["restore", "--configfile", client.ConfigPath])).ShouldSucceed();

        Assert.Equal("1.0.0", NuGetClient.ResolvedVersion(project, PackageSet.BaseId));
        Assert.True(File.Exists(client.GlobalPackagePath(PackageSet.BaseId, "1.0.0")));
    }

    [Fact]
    public async Task EveryNuGetPathAndWorkingDirectory_IsUnderTheTemporaryRoot()
    {
        var client = await ClientAsync(Fixture.Reader);
        var work = client.CreateWorkDirectory("paths");

        var locals = (await client.DotnetAsync(work, ["nuget", "locals", "all", "--list"])).ShouldSucceed();
        var paths = locals.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..])
            .ToList();
        Assert.Equal(4, paths.Count);
        Assert.All(paths, path => Assert.True(TempRoot.IsUnder(path, client.Root), $"{path} is outside the client root {client.Root}."));

        // The configuration files NuGet reads: the isolated ones, and only machine-wide files outside the
        // temporary root (read-only, under the SDK or the system; they cannot be redirected).
        var configPaths = (await client.DotnetAsync(work, ["nuget", "config", "paths", "--working-directory", work])).ShouldSucceed()
            .StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains(client.ConfigPath, configPaths, StringComparer.OrdinalIgnoreCase);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var path in configPaths)
        {
            Assert.False(TempRoot.IsUnder(path, Repository.Root), $"NuGet reads {path}, inside the repository.");
            if (!TempRoot.IsUnder(path, client.Root))
            {
                Assert.False(userProfile.Length > 0 && TempRoot.IsUnder(path, userProfile), $"NuGet reads the user's own {path}.");
                Assert.False(appData.Length > 0 && TempRoot.IsUnder(path, appData), $"NuGet reads the user's own {path}.");
            }
        }

        // Everything the harness writes is below the temporary root, which is outside the repository.
        Assert.False(TempRoot.IsUnder(TempRoot.Location, Repository.Root));
        Assert.True(TempRoot.Contains(client.Root));
        Assert.True(TempRoot.Contains(work));
        Assert.True(TempRoot.Contains(Fixture.Server.StorageDirectory));
        Assert.True(TempRoot.Contains(await ServerBuild.GetAsync()));
        foreach (var (name, value) in client.Environment.Where(v => !string.IsNullOrEmpty(v.Value) && Path.IsPathRooted(v.Value)))
        {
            Assert.True(TempRoot.IsUnder(value!, client.Root), $"{name}={value} is outside the client root.");
        }
    }

    [Fact]
    public async Task ASecretFedThroughTheRunner_IsRedactedFromEverythingLogged()
    {
        var secret = Secrets.Register("e2e-secret-" + Guid.NewGuid().ToString("N"));
        var recorder = new RecordingOutput();
        using var client = new NuGetClient(recorder);

        // dotnet repeats an unknown command name in its error: the secret really goes through the process.
        var result = await client.DotnetAsync(client.Root, ["known-" + secret]);

        Assert.Contains(secret, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.Describe(), StringComparison.Ordinal);
        Assert.Contains(Secrets.Mask, result.Describe(), StringComparison.Ordinal);
        Assert.NotEmpty(recorder.Text);
        Assert.DoesNotContain(secret, recorder.Text, StringComparison.Ordinal);

        // The Basic header value of a registered password, and anything shaped like an API key, too.
        Assert.Equal(Secrets.Mask, Secrets.Redact(Secrets.BasicValue(Fixture.Reader.UserName, Fixture.Reader.Password)));
        Assert.Equal(Secrets.Mask, Secrets.Redact("epq_AAAAAAAAAAAA_0000000000000000000000000000000000000000000"));
    }

    [Fact]
    public async Task ACommandThatHangs_IsKilledAtItsTimeout()
    {
        // A server that accepts connections and never answers: the client waits until the harness kills it.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = new List<Socket>();
        using var stop = new CancellationTokenSource();
        var acceptLoop = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                accepted.Add(await listener.AcceptSocketAsync(stop.Token));
            }
        });

        var client = BareClient();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        (await client.AddSourceAsync(new Uri($"http://127.0.0.1:{port}/v3/index.json"), credentials: null)).ShouldSucceed();
        var stopwatch = Stopwatch.StartNew();
        var result = await client.DotnetAsync(
            client.Root,
            ["package", "search", "anything", "--source", NuGetClient.SourceName, "--configfile", client.ConfigPath],
            TimeSpan.FromSeconds(15));

        Assert.True(result.TimedOut, result.Describe());
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60));
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(result.ProcessId));

        await stop.CancelAsync();
        listener.Stop();
        await Assert.ThrowsAnyAsync<Exception>(() => acceptLoop);
        accepted.ForEach(s => s.Dispose());
    }

    [Fact]
    public async Task DisposingAServer_KillsItsProcess_AndDeletesItsData()
    {
        var server = new ServerProcess(await ServerBuild.GetAsync());
        await server.StartAsync();
        var processId = server.ProcessId;
        var data = server.DataDirectory;
        Assert.True(Directory.Exists(data));

        await server.DisposeAsync();

        Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
        Assert.False(Directory.Exists(data));
    }

    [Fact]
    public async Task AMissingPrerequisite_FailsWithAnActionableMessage()
    {
        var missing = Path.Combine(TempRoot.Location, "missing", "ElPaqueto.csproj");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ServerBuild.PublishAsync(missing));

        Assert.StartsWith("Prerequisite missing:", error.Message, StringComparison.Ordinal);
    }

    public sealed class Setup : ServerFixture
    {
    }

    private sealed class RecordingOutput : ITestOutputHelper
    {
        private readonly StringBuilder _text = new();

        public string Text => _text.ToString();

        public void WriteLine(string message) => _text.AppendLine(message);

        public void WriteLine(string format, params object[] args) => _text.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));
    }
}
