using System.Diagnostics;
using System.Text;

namespace ElPaqueto.Security.Tests;

/// <summary>
/// Runs the real, built host (<c>ElPaqueto.dll</c>) as a child process.
/// </summary>
internal sealed class HostProcess : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    private readonly Process _process;
    private readonly StringBuilder _output = new();

    private HostProcess(string[] arguments, IReadOnlyDictionary<string, string> environment, string? standardInput)
    {
        var hostDll = LocateHostAssembly();

        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(hostDll)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(hostDll);
        foreach (var argument in arguments.Where(a => a.Length > 0))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_NOLOGO"] = "true";
        startInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("The host did not start.");
        _process.OutputDataReceived += (_, e) => Append(e.Data);
        _process.ErrorDataReceived += (_, e) => Append(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // Standard input is always redirected, so it is never a terminal, and closed after the given
        // text: a process that reads it gets end-of-file instead of waiting forever.
        try
        {
            if (standardInput is not null)
            {
                _process.StandardInput.Write(standardInput);
            }

            _process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The host exited before reading its input: its exit code tells the test what happened.
        }
    }

    /// <summary>Everything the host wrote so far.</summary>
    public string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    /// <summary>Starts the host with the given arguments and environment variables.</summary>
    public static HostProcess Start(string[] arguments, IReadOnlyDictionary<string, string> environment)
        => new(arguments, environment, standardInput: null);

    /// <summary>Starts the host with the given arguments, environment variables and standard input.</summary>
    public static HostProcess Start(string[] arguments, IReadOnlyDictionary<string, string> environment, string? standardInput)
        => new(arguments, environment, standardInput);

    /// <summary>Whether the host has exited.</summary>
    public bool HasExited => _process.HasExited;

    /// <summary>Waits for the host to exit on its own and returns its exit code.</summary>
    public async Task<int> WaitForExitAsync(TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? DefaultTimeout);

        try
        {
            await _process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _process.Kill(entireProcessTree: true);
            Assert.Fail($"The host did not exit in time. Output:{Environment.NewLine}{Output}");
        }

        // Make sure the asynchronous output handlers have flushed.
        _process.WaitForExit();
        return _process.ExitCode;
    }

    /// <summary>Waits until the host prints the given text, or fails when it exits or times out.</summary>
    public async Task WaitForOutputAsync(string text, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);

        while (DateTime.UtcNow < deadline)
        {
            if (Output.Contains(text, StringComparison.Ordinal))
            {
                return;
            }

            if (_process.HasExited)
            {
                Assert.Fail($"The host exited with code {_process.ExitCode} before printing '{text}'. Output:{Environment.NewLine}{Output}");
            }

            await Task.Delay(100);
        }

        Assert.Fail($"The host did not print '{text}' in time. Output:{Environment.NewLine}{Output}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }

        _process.Dispose();
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_output)
        {
            _output.AppendLine(line);
        }
    }

    private static string LocateHostAssembly()
    {
        // tests/ElPaqueto.Security.Tests/bin/<Configuration>/<framework>/
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var framework = baseDirectory.Name;
        var configuration = baseDirectory.Parent!.Name;

        var root = baseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ElPaqueto.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);

        var hostDll = Path.Combine(root.FullName, "src", "ElPaqueto", "bin", configuration, framework, "ElPaqueto.dll");
        Assert.True(File.Exists(hostDll), $"The host has not been built: {hostDll}");
        return hostDll;
    }
}
