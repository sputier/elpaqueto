using System.Security.Cryptography;

namespace ElPaqueto.EndToEnd.Tests.Harness;

/// <summary>
/// The real <c>nuget.exe</c>, downloaded once per run from dist.nuget.org at a pinned version and checked against
/// a hard-coded SHA-256 before it is ever started. Nothing else is trusted: a changed file at the same URL fails
/// every nuget.exe test with a message that says so.
/// </summary>
internal static class NuGetExe
{
    /// <summary>The pinned release. Changing it means downloading the new file, checking it, and updating <see cref="Sha256"/>.</summary>
    public const string Version = "7.9.0";

    /// <summary>SHA-256 of <c>https://dist.nuget.org/win-x86-commandline/v7.9.0/nuget.exe</c>, in lowercase hexadecimal.</summary>
    public const string Sha256 = "992d70cac5b06c38efec91806caba64cdcc07e6d963a0959dbbbaf264d33b800";

    public static readonly Uri DownloadUrl = new($"https://dist.nuget.org/win-x86-commandline/v{Version}/nuget.exe");

    private static readonly Lazy<Task<string>> Downloaded = new(DownloadAsync);

    /// <summary>The path of the verified <c>nuget.exe</c>, below the run's temporary directory.</summary>
    public static Task<string> GetPathAsync() => Downloaded.Value;

    /// <summary>The SHA-256 of a file, in lowercase hexadecimal.</summary>
    public static string HashOf(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>Throws when the content is not the pinned file.</summary>
    public static void Verify(byte[] content)
    {
        var actual = HashOf(content);
        if (!string.Equals(actual, Sha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Prerequisite failed: {DownloadUrl} has SHA-256 {actual}, expected {Sha256}. "
                + "The file at the pinned URL changed or the download is corrupt: nuget.exe is not started. "
                + "To move to another release, download it, check it, and update Version and Sha256 in Harness/NuGetExe.cs.");
        }
    }

    private static async Task<string> DownloadAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        byte[] content;
        try
        {
            content = await http.GetByteArrayAsync(DownloadUrl);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException($"Prerequisite missing: nuget.exe {Version} could not be downloaded from {DownloadUrl} ({e.Message}). The nuget.exe tests need Internet access.", e);
        }

        Verify(content);
        var directory = TempRoot.CreateDirectory("nx");
        var path = Path.Combine(directory, "nuget.exe");
        await File.WriteAllBytesAsync(path, content);
        return path;
    }
}

/// <summary>The reason shown for a scenario that needs Windows and runs elsewhere.</summary>
internal static class WindowsOnly
{
    public const string Reason = "nuget.exe and Visual Studio's MSBuild are Windows programs: this scenario runs on Windows only. "
        + "nuget.exe on Mono is not verified (docs/user/client-compatibility.md).";
}

/// <summary>An xUnit fact that runs on Windows only; elsewhere it is reported as skipped, with the reason.</summary>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = WindowsOnly.Reason;
        }
    }
}

/// <summary>An xUnit theory that runs on Windows only; elsewhere it is reported as skipped, with the reason.</summary>
internal sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = WindowsOnly.Reason;
        }
    }
}
