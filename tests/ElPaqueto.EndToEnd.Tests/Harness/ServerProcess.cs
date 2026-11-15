using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ElPaqueto.EndToEnd.Tests.Harness;

/// <summary>
/// The published server, run as <c>dotnet ElPaqueto.dll</c> in a child process, on <c>127.0.0.1</c> and a
/// port chosen by the system (read from the <c>Now listening on:</c> log line), with fresh data in its own
/// temporary directory. Restarts reuse the port and the data. Every setting is passed through environment
/// variables; nothing from the machine's environment that could change the configuration is inherited.
/// </summary>
internal sealed partial class ServerProcess : IServerUnderTest
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    // Every server still running: killed when the test process exits, even if no fixture was disposed
    // (a cancelled run, a hang detector, a fixture whose initialization failed).
    private static readonly ConcurrentDictionary<int, Process> Running = new();

    private readonly string _serverDirectory;
    private readonly string _dataDirectory;
    private readonly Dictionary<string, string?> _environment;
    private readonly StringBuilder _log = new();
    private Process? _process;
    private int _port;

    static ServerProcess()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var process in Running.Values)
            {
                CommandRunner.KillTree(process);
            }
        };
    }

    public ServerProcess(string serverDirectory, IReadOnlyDictionary<string, string?>? settings = null)
    {
        _serverDirectory = serverDirectory;
        _dataDirectory = TempRoot.CreateDirectory("srv");
        StorageDirectory = Path.Combine(_dataDirectory, "storage");
        DataDirectory = _dataDirectory;
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "home"));
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "tmp"));

        _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            // Nothing the server or the admin commands write (data protection keys, first-run files, temporary
            // files) may land in the user's profile.
            ["HOME"] = Path.Combine(_dataDirectory, "home"),
            ["USERPROFILE"] = Path.Combine(_dataDirectory, "home"),
            ["DOTNET_CLI_HOME"] = Path.Combine(_dataDirectory, "home"),
            ["TMP"] = Path.Combine(_dataDirectory, "tmp"),
            ["TEMP"] = Path.Combine(_dataDirectory, "tmp"),
            ["TMPDIR"] = Path.Combine(_dataDirectory, "tmp"),
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            // appsettings.json is read from the published directory, the working directory of every run.
            ["ELPAQUETO_CONFIG_ROOT"] = serverDirectory,
            ["Security__Database__ConnectionString"] = "Data Source=" + Path.Combine(_dataDirectory, "auth.db"),
            ["Database__Type"] = "Sqlite",
            ["Database__ConnectionString"] = "Data Source=" + Path.Combine(_dataDirectory, "packages.db"),
            ["Storage__Type"] = "FileSystem",
            ["Storage__Path"] = StorageDirectory,
            ["Search__Type"] = "Database",
            ["Mirror__Enabled"] = "false",
            // The lowest accepted cost: every user created by the tests is hashed, and slow hashes only slow the run.
            ["Security__Passwords__IterationCount"] = "100000",
            // Scenarios fail on purpose many times from 127.0.0.1. Throttling is tested by its own fixture.
            ["Security__Throttling__ClientFailureLimit"] = "1000",
            ["Security__Throttling__PairFailureLimit"] = "1000",
            ["Security__Throttling__AccountFailureLimit"] = "1000",
            ["Logging__Console__LogLevel__Default"] = "Warning",
            ["Logging__Console__LogLevel__Microsoft.Hosting.Lifetime"] = "Information",
            ["Logging__Console__LogLevel__ElPaqueto.Security.Audit"] = "Information",
            // One line per request with its status: what the clients really sent is part of the findings.
            ["Logging__Console__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics"] = "Information",
        };

        foreach (var (name, value) in settings ?? new Dictionary<string, string?>())
        {
            _environment[name] = value;
        }
    }

    public Uri BaseAddress => _port == 0
        ? throw new InvalidOperationException("The server has not been started.")
        : new Uri($"http://127.0.0.1:{_port.ToString(CultureInfo.InvariantCulture)}/");

    public Uri ServiceIndex => new(BaseAddress, "v3/index.json");

    public string StorageDirectory { get; }

    /// <summary>The directory that holds both databases and the storage.</summary>
    public string DataDirectory { get; }

    public bool SupportsGracefulStop => !OperatingSystem.IsWindows();

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>The id of the running server process.</summary>
    public int ProcessId => _process?.Id ?? throw new InvalidOperationException("The server has not been started.");

    public string Log
    {
        get
        {
            lock (_log)
            {
                return _log.ToString();
            }
        }
    }

    public async Task StartAsync()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("The server is already running.");
        }

        var urls = "http://127.0.0.1:" + _port.ToString(CultureInfo.InvariantCulture);
        var environment = new Dictionary<string, string?>(_environment) { ["ASPNETCORE_URLS"] = urls };
        var spec = new CommandSpec(
            CommandRunner.DotnetPath,
            [Path.Combine(_serverDirectory, "ElPaqueto.dll")],
            _serverDirectory,
            environment,
            StartTimeout);

        var logStart = Log.Length;
        _process?.Dispose();
        _process = null;
        var process = new Process { StartInfo = CommandRunner.CreateStartInfo(spec) };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        try
        {
            CommandRunner.Start(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }

        _process = process;
        Running.TryAdd(process.Id, process);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _process.StandardInput.Close();

        var deadline = DateTime.UtcNow + StartTimeout;
        while (true)
        {
            var listening = ListeningOn().Match(Log, logStart);
            if (listening.Success)
            {
                _port = int.Parse(listening.Groups[1].Value, CultureInfo.InvariantCulture);
                break;
            }

            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The server exited with code {_process.ExitCode} before listening." + Environment.NewLine + Secrets.Redact(Log));
            }

            if (DateTime.UtcNow > deadline)
            {
                await KillAsync();
                throw new TimeoutException("The server did not report its address in time." + Environment.NewLine + Secrets.Redact(Log));
            }

            await Task.Delay(100);
        }

        // Ready means the anonymous liveness endpoint answers.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        while (true)
        {
            try
            {
                using var response = await client.GetAsync(new Uri(BaseAddress, "health"));
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            {
                // Not ready yet (refused, or slower than the request timeout).
            }

            if (_process.HasExited || DateTime.UtcNow > deadline)
            {
                await KillAsync();
                throw new TimeoutException("The server never answered /health with 200." + Environment.NewLine + Secrets.Redact(Log));
            }

            await Task.Delay(200);
        }
    }

    public async Task<StopKind> StopAsync()
    {
        var process = _process ?? throw new InvalidOperationException("The server has not been started.");
        if (!SupportsGracefulStop)
        {
            // Windows has no way to send a console close signal to a child process without a console of its
            // own that does not also reach the test runner: the stop is a kill there (documented).
            await KillAsync();
            return StopKind.Killed;
        }

        var signal = await CommandRunner.RunAsync(new CommandSpec(
            "kill",
            ["-TERM", process.Id.ToString(CultureInfo.InvariantCulture)],
            _dataDirectory,
            new Dictionary<string, string?>(),
            TimeSpan.FromSeconds(10)));
        signal.ShouldSucceed();

        using var timeout = new CancellationTokenSource(StopTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            await KillAsync();
            Assert.Fail("The server did not exit within 30 seconds of SIGTERM." + Environment.NewLine + Secrets.Redact(Log));
        }

        Assert.True(process.ExitCode == 0, $"The server exited with code {process.ExitCode} after SIGTERM." + Environment.NewLine + Secrets.Redact(Log));
        Assert.True(Log.Contains("Application is shutting down", StringComparison.Ordinal), "No graceful shutdown in the log." + Environment.NewLine + Secrets.Redact(Log));
        return StopKind.Graceful;
    }

    public Task KillAsync()
    {
        if (_process is not null)
        {
            CommandRunner.KillTree(_process);
            Running.TryRemove(new KeyValuePair<int, Process>(_process.Id, _process));
        }

        return Task.CompletedTask;
    }

    public Task<CommandResult> RunAdminAsync(IReadOnlyList<string> arguments, string? standardInput = null)
        => CommandRunner.RunAsync(new CommandSpec(
            CommandRunner.DotnetPath,
            [Path.Combine(_serverDirectory, "ElPaqueto.dll"), "admin", .. arguments],
            _serverDirectory,
            _environment,
            TimeSpan.FromMinutes(2),
            standardInput));

    public async ValueTask DisposeAsync()
    {
        await KillAsync();
        _process?.Dispose();
        _process = null;
        TempRoot.TryDelete(_dataDirectory);
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_log)
        {
            _log.AppendLine(line);
        }
    }

    [GeneratedRegex(@"Now listening on: http://127\.0\.0\.1:(\d+)")]
    private static partial Regex ListeningOn();
}
