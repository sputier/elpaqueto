using ElPaqueto.Authentication;
using ElPaqueto.Core;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Authentication;
using ElPaqueto.Security.Authorization;
using ElPaqueto.Security.Cli;
using ElPaqueto.Security.Configuration;
using ElPaqueto.Security.Data;
using ElPaqueto.Security.Passwords;
using ElPaqueto.Security.Transport;
using ElPaqueto.Security.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// Registers the validated <c>Security</c> options, the security database, which is migrated
    /// before the server accepts requests, password hashing, the API key service, authentication, and the
    /// trusted-proxy configuration used by <see cref="ElPaquetoSecurityPipelineExtensions.UseElPaquetoForwardedHeaders"/>.
    /// Authorization is added by a later change behind this single entry point.
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

        services.AddSingleton<IPasswordHasher<User>>(provider =>
        {
            var passwords = provider.GetRequiredService<IOptions<ElPaquetoSecurityOptions>>().Value.Passwords;

            return new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
            {
                CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
                IterationCount = passwords.IterationCount,
            }));
        });
        services.AddSingleton<IPasswordTimingPad, Pbkdf2TimingPad>();
        services.AddSingleton<IPasswordService, PasswordService>();

        // The scheme and policy names are the ones the inherited controllers already reference, so that
        // no controller has to change (ADR 0002, decision 3).
        services.AddSingleton<VerificationCache>();
        services
            .AddAuthentication(authentication =>
            {
                authentication.DefaultAuthenticateScheme = AuthenticationConstants.NugetBasicAuthenticationScheme;
                authentication.DefaultChallengeScheme = AuthenticationConstants.NugetBasicAuthenticationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, ElPaquetoBasicAuthenticationHandler>(
                AuthenticationConstants.NugetBasicAuthenticationScheme,
                displayName: null,
                configureOptions: null);

        // Deny by default: the fallback policy, the default policy and the policy the controllers name are the
        // same single requirement, which looks the endpoint up in the permission table (ADR 0002, decisions 4 and 5).
        services.AddHttpContextAccessor();
        services.AddSingleton<IAuthorizationHandler, EndpointPermissionHandler>();
        services.AddAuthorization(authorization =>
        {
            var policy = new AuthorizationPolicyBuilder().AddRequirements(new EndpointPermissionRequirement()).Build();

            authorization.DefaultPolicy = policy;
            authorization.FallbackPolicy = policy;
            authorization.AddPolicy(AuthenticationConstants.NugetUserPolicy, policy);
        });
        services.AddSingleton<IHostedService, EndpointInventoryStartupCheck>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IApiKeyHashComparer, FixedTimeApiKeyHashComparer>();
        services.AddSingleton<ApiKeyUsageTracker>();
        services.AddScoped<IApiKeyService, ApiKeyService>();

        services.AddSingleton<IPostConfigureOptions<ForwardedHeadersOptions>, ConfigureTrustedForwardedHeaders>();
        services.AddSingleton<IHostedService, TransportStartupWarnings>();

        // User administration, used by the admin commands.
        services.TryAddSingleton<ISecretPrompt, ConsoleSecretPrompt>();
        services.AddScoped<UserService>();

        return app;
    }
}
