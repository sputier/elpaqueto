using ElPaqueto.Core;
using ElPaqueto.Security.Configuration;
using ElPaqueto.Security.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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
    /// Registers the validated <c>Security</c> options and the security database, which is migrated
    /// before the server accepts requests. Authentication, authorization and API keys are added by
    /// later changes behind this single entry point.
    /// </remarks>
    public static ElPaquetoApplication AddElPaquetoSecurity(this ElPaquetoApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var services = app.Services;

        services.AddElPaquetoOptions<ElPaquetoSecurityOptions>(ElPaquetoSecurityOptions.SectionName);
        services.AddSingleton<IValidateOptions<ElPaquetoSecurityOptions>, SecurityConfigurationShapeValidator>();

        // Deliberately not an IContext: the inherited provider pipeline must never resolve this database.
        services.AddDbContext<AuthDbContext>((provider, builder) =>
        {
            var connectionString = provider.GetRequiredService<IOptions<ElPaquetoSecurityOptions>>()
                .Value.Database.ConnectionString;

            AuthDbContextConfiguration.ConfigureCore(builder, connectionString);
        });

        services.AddSingleton<IAuthDatabaseMigrator, AuthDatabaseMigrator>();
        services.AddSingleton<IHostedService, AuthDatabaseStartupService>();

        return app;
    }
}
