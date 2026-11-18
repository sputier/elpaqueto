using System.Diagnostics;

namespace ElPaqueto.EndToEnd.Tests.Harness;

/// <summary>
/// The MSBuild of a Visual Studio installation, found with <c>vswhere</c>: it is what Visual Studio runs for
/// <c>msbuild -t:restore</c> and for the restore of the Package Manager. The IDE itself cannot be driven
/// (see docs/user/client-checklist.md).
/// </summary>
internal static class VisualStudioMSBuild
{
    private static readonly Lazy<(string? Path, string Description)> Found = new(Find);

    /// <summary>The full path of <c>MSBuild.exe</c>, or null when no Visual Studio (or Build Tools) is installed.</summary>
    public static string? Path => Found.Value.Path;

    /// <summary>The installation found, or why none was.</summary>
    public static string Description => Found.Value.Description;

    private static (string? Path, string Description) Find()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, "not Windows");
        }

        var programFiles = System.Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        var vswhere = programFiles is null ? null : System.IO.Path.Combine(programFiles, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (vswhere is null || !File.Exists(vswhere))
        {
            return (null, "vswhere.exe not found: no Visual Studio installation");
        }

        var startInfo = new ProcessStartInfo(vswhere)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // 17.14 is the first Visual Studio whose MSBuild restores a net10.0 project (SDK 10).
        foreach (var argument in new[] { "-latest", "-prerelease", "-version", "[17.14,)", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        string output;
        try
        {
            using var process = Process.Start(startInfo)!;
            var read = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                process.Kill(entireProcessTree: true);
                return (null, "vswhere did not answer in 60 s");
            }

            output = read.GetAwaiter().GetResult();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (null, "vswhere could not be started: " + e.Message);
        }

        var path = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(File.Exists);
        return path is null ? (null, "vswhere found no MSBuild.exe") : (path, path);
    }
}

/// <summary>A Windows fact that also needs a Visual Studio (or Build Tools) installation; otherwise it is skipped, with the reason.</summary>
internal sealed class VisualStudioFactAttribute : FactAttribute
{
    public VisualStudioFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = WindowsOnly.Reason;
        }
        else if (VisualStudioMSBuild.Path is null)
        {
            Skip = "No Visual Studio installation with MSBuild was found on this machine (" + VisualStudioMSBuild.Description + ").";
        }
    }
}
