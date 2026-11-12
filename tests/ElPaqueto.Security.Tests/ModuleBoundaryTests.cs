using System.Text.Json;

namespace ElPaqueto.Security.Tests;

/// <summary>
/// Keeps the module boundary of the security project enforceable: it may depend on the core
/// project and the ASP.NET Core shared framework, never on the web layer or a database provider.
/// </summary>
/// <remarks>
/// The boundary is a project-graph rule, so it is checked on the restored project graph
/// (direct and transitive project references), not on the assembly references the compiler
/// happens to emit, which only list types that are actually used.
/// </remarks>
public class ModuleBoundaryTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "ElPaqueto.Web",
        "ElPaqueto.Database.",
    ];

    [Fact]
    public void SecurityProject_DoesNotDependOnWebOrDatabaseProjects()
    {
        var assetsPath = Path.Combine(SolutionRoot(), "src", "ElPaqueto.Security", "obj", "project.assets.json");
        Assert.True(File.Exists(assetsPath), $"The project has not been restored: {assetsPath}");

        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var projects = assets.RootElement
            .GetProperty("libraries")
            .EnumerateObject()
            .Where(library => library.Value.GetProperty("type").GetString() == "project")
            .Select(library => library.Name.Split('/')[0])
            .ToList();

        Assert.Contains("ElPaqueto.Core", projects);

        var violations = projects
            .Where(name => ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void SecurityAssembly_DoesNotReferenceWebOrDatabaseAssemblies()
    {
        var referenced = typeof(ElPaquetoSecurityApplicationExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty);

        var violations = referenced
            .Where(name => ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(violations);
    }

    private static string SolutionRoot()
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
