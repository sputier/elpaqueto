namespace ElPaqueto.Security.Tests;

/// <summary>
/// Keeps the module boundary of the security project enforceable: it may depend on the core
/// project and the ASP.NET Core shared framework, never on the web layer or a database provider.
/// </summary>
public class ModuleBoundaryTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "ElPaqueto.Web",
        "ElPaqueto.Database.",
    ];

    [Fact]
    public void SecurityAssembly_DoesNotReferenceWebOrDatabaseProviders()
    {
        var referenced = typeof(ElPaquetoSecurityApplicationExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        var violations = referenced
            .Where(name => ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void SecurityAssembly_ReferencesTheCoreProject()
    {
        var referenced = typeof(ElPaquetoSecurityApplicationExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name);

        Assert.Contains("ElPaqueto.Core", referenced);
    }
}
