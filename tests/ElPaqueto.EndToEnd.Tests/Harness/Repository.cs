using System.Reflection;

namespace ElPaqueto.EndToEnd.Tests.Harness;

/// <summary>Where the repository and the built server are. Only read, never written, by the tests.</summary>
internal static class Repository
{
    private static readonly Lazy<string> RootDirectory = new(FindRoot);

    /// <summary>The repository root: the directory that holds <c>ElPaqueto.sln</c>.</summary>
    public static string Root => RootDirectory.Value;

    /// <summary>The server project, published by <see cref="ServerBuild"/>.</summary>
    public static string ServerProject => Path.Combine(Root, "src", "ElPaqueto", "ElPaqueto.csproj");

    /// <summary>The repository's <c>global.json</c>, copied into every client root to pin the client SDK.</summary>
    public static string GlobalJson => Path.Combine(Root, "global.json");

    /// <summary>The build configuration of this test assembly, which is also the one of the server it publishes.</summary>
    public static string Configuration
        => typeof(Repository).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
            ?? throw new InvalidOperationException("The test assembly has no configuration attribute.");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ElPaqueto.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                $"Prerequisite missing: ElPaqueto.sln was not found above {AppContext.BaseDirectory}. Run the tests from a clone of the repository.");
    }
}
