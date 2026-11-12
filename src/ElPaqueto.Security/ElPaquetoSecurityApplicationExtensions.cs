using ElPaqueto.Core;
using ElPaqueto.Security.Configuration;

namespace ElPaqueto.Security;

/// <summary>
/// Entry point of the ElPaqueto security module.
/// </summary>
public static class ElPaquetoSecurityApplicationExtensions
{
    /// <summary>
    /// Adds the ElPaqueto security module to the application.
    /// </summary>
    /// <param name="app">The application to extend.</param>
    /// <returns>The same <paramref name="app"/> instance, to allow chaining.</returns>
    /// <remarks>
    /// Registers the validated <c>Security</c> options. Authentication, authorization and API keys
    /// are added by later changes behind this single entry point.
    /// </remarks>
    public static ElPaquetoApplication AddElPaquetoSecurity(this ElPaquetoApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Services.AddElPaquetoOptions<ElPaquetoSecurityOptions>(ElPaquetoSecurityOptions.SectionName);

        return app;
    }
}
