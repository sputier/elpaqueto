using Microsoft.AspNetCore.HttpLogging;

namespace ElPaqueto.Security.Tests.Auditing;

/// <summary>
/// Guards against the usual ways request credentials end up in logs: request logging middleware and Entity
/// Framework Core's sensitive data logging.
/// </summary>
public sealed class LoggingGuardTests
{
    private static readonly string[] ForbiddenCalls =
    [
        "UseHttpLogging",
        "AddHttpLogging",
        "UseW3CLogging",
        "AddW3CLogging",
        "EnableSensitiveDataLogging",
    ];

    [Fact]
    public void TheServerSources_NeverEnableRequestLoggingOrSensitiveDataLogging()
    {
        var sources = Path.Combine(RepositoryRoot(), "src");

        var offenders = Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => ForbiddenCalls
                .Where(call => File.ReadAllText(path).Contains(call, StringComparison.Ordinal))
                .Select(call => $"{Path.GetRelativePath(sources, path)}: {call}"))
            .ToList();

        Assert.True(offenders.Count == 0, "See docs/technical/audit-logging.md before enabling: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-NuGet-ApiKey")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Cookie")]
    public void TheDefaultHttpLoggingAllowList_ExcludesCredentialHeaders(string header)
    {
        // The documented guard: HttpLoggingOptions.RequestHeaders is an allow-list (other headers are written as
        // [Redacted]); this pins the .NET default that docs/technical/audit-logging.md describes.
        var options = new HttpLoggingOptions();

        Assert.DoesNotContain(options.RequestHeaders, h => string.Equals(h, header, StringComparison.OrdinalIgnoreCase));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ElPaqueto.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
