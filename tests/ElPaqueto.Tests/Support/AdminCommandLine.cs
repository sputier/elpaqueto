using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ElPaqueto.Tests;

/// <summary>
/// Runs <c>elpaqueto admin ...</c> with the real, built server binary as a child process, against a
/// given security database: the way an operator runs the commands next to a running server.
/// </summary>
internal sealed class AdminCommandLine : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private readonly string _directory;

    public AdminCommandLine()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ElPaquetoTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    /// <summary>The connection string of the security database shared by the commands and the server.</summary>
    public string SecurityConnectionString => $"Data Source={Path.Combine(_directory, "auth.db")}";

    /// <summary>
    /// The password cost used by the commands; the server under test is configured with the same one.
    /// </summary>
    public const string IterationCount = "100000";

    /// <summary>Runs the command, asserts that it exits with <paramref name="expectedExitCode"/> and returns its output.</summary>
    /// <param name="standardInput">Written to standard input, which is then closed.</param>
    /// <param name="expectedExitCode">The exit code the command must return.</param>
    /// <param name="arguments">The arguments, starting with <c>admin</c>.</param>
    public async Task<string> RunAsync(string standardInput, int expectedExitCode, params string[] arguments)
    {
        var hostDll = Path.Combine(AppContext.BaseDirectory, "ElPaqueto.dll");
        Assert.True(File.Exists(hostDll), $"The server binary was not found: {hostDll}");

        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(hostDll);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_NOLOGO"] = "true";

        // The same environment as the server under test, whatever the test process inherited.
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        startInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        startInfo.Environment["Security__Database__ConnectionString"] = SecurityConnectionString;
        startInfo.Environment["Security__Passwords__IterationCount"] = IterationCount;
        startInfo.Environment["Database__Type"] = "Sqlite";
        startInfo.Environment["Database__ConnectionString"] = $"Data Source={Path.Combine(_directory, "packages.db")}";
        startInfo.Environment["Storage__Type"] = "FileSystem";
        startInfo.Environment["Storage__Path"] = Path.Combine(_directory, "packages");

        var output = new StringBuilder();
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The server binary did not start.");
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.StandardInput.WriteAsync(standardInput ?? string.Empty);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The command exited before reading its input: the exit code tells what happened.
        }

        using (var cancellation = new CancellationTokenSource(Timeout))
        {
            try
            {
                await process.WaitForExitAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail($"The command did not exit in time. Output:{Environment.NewLine}{Read(output)}");
            }
        }

        // Make sure the asynchronous output handlers have flushed.
        process.WaitForExit();

        var text = Read(output);
        Assert.True(process.ExitCode == expectedExitCode, $"Expected exit code {expectedExitCode}, got {process.ExitCode}. Output:{Environment.NewLine}{text}");
        return text;
    }

    /// <summary>Deletes the databases. Dispose the server sharing them first.</summary>
    public void Dispose()
    {
        // Pooled connections of the in-process server keep the files open, which blocks deletion on Windows.
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temporary directory does not affect other tests.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }

    private static void Append(StringBuilder output, string line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            output.AppendLine(line);
        }
    }

    private static string Read(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }
}
