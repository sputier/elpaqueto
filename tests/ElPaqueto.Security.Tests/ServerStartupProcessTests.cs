using System.Diagnostics;
using System.Text;

namespace ElPaqueto.Security.Tests;

/// <summary>
/// Starts the real, built host to prove that an invalid security setting stops it with a
/// non-zero exit code before it listens or runs any command.
/// </summary>
public class ServerStartupProcessTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    [Theory]
    [InlineData("")]
    [InlineData("import", "downloads")]
    public async Task InvalidSecuritySetting_StopsTheHostWithANonZeroExitCode(params string[] arguments)
    {
        var result = await RunHostAsync(arguments, new Dictionary<string, string>
        {
            // Whitespace only: an empty value would be treated as unset on some platforms.
            ["Security__Database__ConnectionString"] = "   ",
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Security:Database:ConnectionString", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Now listening", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InheritedAndSecuritySettings_AreValidatedAndBothReported()
    {
        var result = await RunHostAsync([], new Dictionary<string, string>
        {
            ["Database__Type"] = "NotADatabase",
            ["Security__Database__ConnectionString"] = "   ",
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Database:Type", result.Output, StringComparison.Ordinal);
        Assert.Contains("Security:Database:ConnectionString", result.Output, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output)> RunHostAsync(
        string[] arguments,
        Dictionary<string, string> environment)
    {
        var hostDll = LocateHostAssembly();

        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(hostDll)!,
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

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The host did not start.");
        using var cancellation = new CancellationTokenSource(Timeout);

        var output = new StringBuilder();
        void Append(string? line)
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

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The host kept running with an invalid security setting.");
        }

        // Make sure the asynchronous output handlers have flushed.
        process.WaitForExit();

        lock (output)
        {
            return (process.ExitCode, output.ToString());
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
