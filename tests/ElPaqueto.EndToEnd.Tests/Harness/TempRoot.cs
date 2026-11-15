using System.Security.Cryptography;

namespace ElPaqueto.EndToEnd.Tests.Harness;

/// <summary>
/// The one temporary directory of a test run. Every file the harness writes (published server, server data,
/// NuGet folders, generated projects and packages) is created below it, never in the repository or in the
/// user's profile. It is deleted when the test process exits; each fixture and client also deletes its own
/// subdirectory when it is disposed.
/// </summary>
internal static class TempRoot
{
    private static readonly Lazy<string> Root = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The full path of the run's temporary directory.</summary>
    public static string Location => Root.Value;

    /// <summary>Creates a new, empty directory below the root. Names stay short: Windows paths are limited.</summary>
    public static string CreateDirectory(string prefix)
    {
        var path = Path.Combine(Location, prefix + "-" + RandomNumberGenerator.GetHexString(6, lowercase: true));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Whether the path is below the run's temporary directory.</summary>
    public static bool Contains(string path)
        => IsUnder(path, Location);

    /// <summary>Whether <paramref name="path"/> is <paramref name="directory"/> or below it.</summary>
    public static bool IsUnder(string path, string directory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return full.Equals(root, PathComparison)
            || full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>Deletes a directory, retrying while a just-stopped process still holds a file.</summary>
    public static void TryDelete(string directory)
    {
        for (var attempt = 0; attempt < 10 && Directory.Exists(directory); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
        }
    }

    private static string Create()
    {
        // Path.GetTempPath() honours TMPDIR/TEMP, so a CI system can move it. Directory.CreateTempSubdirectory
        // creates the directory with owner-only permissions on Unix.
        var root = Directory.CreateTempSubdirectory("epq-e2e-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(root);
        return root;
    }
}
