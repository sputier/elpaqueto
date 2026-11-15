namespace ElPaqueto.Security.Tests;

/// <summary>
/// Starts the real, built host to prove that an invalid security setting stops it with a
/// non-zero exit code before it listens or runs any command.
/// </summary>
public class ServerStartupProcessTests
{
    [Theory]
    [InlineData("")]
    [InlineData("import", "downloads")]
    public async Task InvalidSecuritySetting_StopsTheHostWithANonZeroExitCode(params string[] arguments)
    {
        using var host = HostProcess.Start(arguments, new Dictionary<string, string>
        {
            // Whitespace only: an empty value would be treated as unset on some platforms.
            ["Security__Database__ConnectionString"] = "   ",
        });

        var exitCode = await host.WaitForExitAsync();

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Security:Database:ConnectionString", host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Now listening", host.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InheritedAndSecuritySettings_AreValidatedAndBothReported()
    {
        using var host = HostProcess.Start([], new Dictionary<string, string>
        {
            ["Database__Type"] = "NotADatabase",
            ["Security__Database__ConnectionString"] = "   ",
        });

        var exitCode = await host.WaitForExitAsync();

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Database:Type", host.Output, StringComparison.Ordinal);
        Assert.Contains("Security:Database:ConnectionString", host.Output, StringComparison.Ordinal);
    }
}
