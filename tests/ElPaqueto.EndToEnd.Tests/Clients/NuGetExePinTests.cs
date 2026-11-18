using ElPaqueto.EndToEnd.Tests.Harness;

namespace ElPaqueto.EndToEnd.Tests.Clients;

/// <summary>The pinned nuget.exe is never started when its SHA-256 differs. Needs neither Windows nor the network.</summary>
public sealed class NuGetExePinTests
{
    [Fact]
    public void Verify_RejectsAFileWhoseHashIsNotThePinnedOne()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => NuGetExe.Verify([1, 2, 3]));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
        Assert.Contains(NuGetExe.Sha256, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PinnedHash_IsAValidSha256_AndTheUrlNamesThePinnedVersion()
    {
        Assert.Matches("^[0-9a-f]{64}$", NuGetExe.Sha256);
        Assert.Equal($"https://dist.nuget.org/win-x86-commandline/v{NuGetExe.Version}/nuget.exe", NuGetExe.DownloadUrl.AbsoluteUri);
        Assert.Equal(NuGetExe.Sha256.Length, NuGetExe.HashOf([]).Length);
    }
}
