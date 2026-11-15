using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ElPaqueto.EndToEnd.Tests.Harness;

/// <summary>What to run: a program, its arguments, where, with which environment changes and for how long.</summary>
internal sealed record CommandSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    TimeSpan Timeout,
    string? StandardInput = null);

/// <summary>The outcome of a command. The raw streams are kept for assertions; only <see cref="Describe"/> is logged.</summary>
internal sealed class CommandResult
{
    public required string CommandLine { get; init; }

    public required int ProcessId { get; init; }

    public required int ExitCode { get; init; }

    public required bool TimedOut { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>Standard output, unredacted: never write it to the test output as it is.</summary>
    public required string StandardOutput { get; init; }

    /// <summary>Standard error, unredacted: never write it to the test output as it is.</summary>
    public required string StandardError { get; init; }

    /// <summary>Both streams, unredacted.</summary>
    public string Output => StandardOutput + StandardError;

    /// <summary>A redacted description: the command, its exit code, its duration and its output.</summary>
    public string Describe()
    {
        var builder = new StringBuilder();
        builder.Append("$ ").AppendLine(CommandLine);
        builder.Append("exit code ").Append(ExitCode).Append(" after ").Append(Duration.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(" s");
        if (TimedOut)
        {
            builder.Append(" (TIMED OUT: the process tree was killed)");
        }

        builder.AppendLine();
        if (StandardOutput.Length > 0)
        {
            builder.AppendLine("--- standard output").Append(StandardOutput);
        }

        if (StandardError.Length > 0)
        {
            builder.AppendLine("--- standard error").Append(StandardError);
        }

        return Secrets.Redact(builder.ToString());
    }

    /// <summary>Asserts that the command finished in time with exit code 0.</summary>
    public CommandResult ShouldSucceed()
    {
        Assert.False(TimedOut, "The command timed out." + Environment.NewLine + Describe());
        Assert.True(ExitCode == 0, "The command failed." + Environment.NewLine + Describe());
        return this;
    }

    /// <summary>Asserts that the command finished in time with a non-zero exit code.</summary>
    public CommandResult ShouldFail()
    {
        Assert.False(TimedOut, "The command timed out instead of failing: it may have waited for input." + Environment.NewLine + Describe());
        Assert.True(ExitCode != 0, "The command succeeded although it should have failed." + Environment.NewLine + Describe());
        return this;
    }

    /// <summary>Asserts that the output contains the text.</summary>
    public CommandResult ShouldContain(string text)
    {
        Assert.True(Output.Contains(text, StringComparison.Ordinal), $"The output does not contain '{Secrets.Redact(text)}'." + Environment.NewLine + Describe());
        return this;
    }

    /// <summary>Asserts that the output does not contain the text.</summary>
    public CommandResult ShouldNotContain(string text)
    {
        Assert.False(Output.Contains(text, StringComparison.OrdinalIgnoreCase), $"The output contains '{Secrets.Redact(text)}'." + Environment.NewLine + Describe());
        return this;
    }
}

/// <summary>
/// Runs a child process with redirected streams, standard input closed after the given text (so nothing
/// can wait for a terminal), and a timeout after which the whole process tree is killed.
/// </summary>
internal static class CommandRunner
{
    // Variables of the machine or of the test runner that would change what a child process does.
    private static readonly string[] InheritedPrefixesRemoved =
    [
        "NUGET_", "NuGetPackageSourceCredentials_", "MSBuild", "MSBUILD", "VSTEST", "DOTNET_CLI_HOME",
        "DOTNET_HOST_PATH", "ASPNETCORE_", "Security__", "Database__", "Storage__", "Search__", "Mirror__",
        "Logging__", "Statistics__", "HealthCheck__", "ELPAQUETO_", "BAGET_", "Authentication__", "ApiKey",
        "PackageDeletionBehavior", "AllowPackageOverwrites", "Kestrel__", "AllowedHosts", "PathBase",
    ];

    /// <summary>The <c>dotnet</c> executable that runs the tests, so that every child uses the same installation.</summary>
    public static string DotnetPath
    {
        get
        {
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            return !string.IsNullOrEmpty(host) && File.Exists(host) ? host : "dotnet";
        }
    }

    public static async Task<CommandResult> RunAsync(CommandSpec spec)
    {
        var startInfo = CreateStartInfo(spec);
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => Append(standardOutput, e.Data);
        process.ErrorDataReceived += (_, e) => Append(standardError, e.Data);

        var commandLine = Describe(spec);
        var stopwatch = Stopwatch.StartNew();
        Start(process);

        var processId = process.Id;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            if (spec.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(spec.StandardInput);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process exited before reading its input: its exit code tells what happened.
        }

        var timedOut = false;
        using (var timeout = new CancellationTokenSource(spec.Timeout))
        {
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                KillTree(process);
            }
        }

        stopwatch.Stop();
        lock (standardOutput)
        {
            lock (standardError)
            {
                return new CommandResult
                {
                    CommandLine = commandLine,
                    ProcessId = processId,
                    ExitCode = process.HasExited ? process.ExitCode : -1,
                    TimedOut = timedOut,
                    Duration = stopwatch.Elapsed,
                    StandardOutput = standardOutput.ToString(),
                    StandardError = standardError.ToString(),
                };
            }
        }
    }

    /// <summary>The start information of a command: redirected streams, filtered environment, the spec's changes.</summary>
    public static ProcessStartInfo CreateStartInfo(CommandSpec spec)
    {
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var name in startInfo.Environment.Keys.Where(IsRemoved).ToList())
        {
            startInfo.Environment.Remove(name);
        }

        foreach (var (name, value) in spec.Environment)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        return startInfo;
    }

    /// <summary>Starts a process, turning a missing executable into an actionable message.</summary>
    public static void Start(Process process)
    {
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException($"Prerequisite missing: '{process.StartInfo.FileName}' could not be started ({e.Message}).", e);
        }
    }

    /// <summary>The command line of a spec, for logs (redact before writing it anywhere).</summary>
    public static string Describe(CommandSpec spec)
        => Quote(spec.FileName == DotnetPath ? "dotnet" : spec.FileName) + " " + string.Join(' ', spec.Arguments.Select(Quote));

    /// <summary>Kills a process and all its descendants, ignoring a process that is already gone.</summary>
    public static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // Never started, or already exited (Kill throws for a process that is gone).
        }
    }

    private static bool IsRemoved(string name)
        => InheritedPrefixesRemoved.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (builder)
        {
            builder.AppendLine(line);
        }
    }

    private static string Quote(string argument)
        => argument.Length > 0 && !argument.Any(char.IsWhiteSpace) ? argument : "\"" + argument + "\"";
}
