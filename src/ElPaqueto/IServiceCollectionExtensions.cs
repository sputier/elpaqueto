using ElPaqueto.Authentication;
using ElPaqueto.Core;
using ElPaqueto.Web.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace ElPaqueto;

internal static class IServiceCollectionExtensions
{
    internal static ElPaquetoApplication AddNugetBasicHttpAuthentication(this ElPaquetoApplication app)
    {
        app.Services.AddAuthentication(options =>
        {
            // Breaks existing tests if the contains check is not here.
            if (!options.SchemeMap.ContainsKey(AuthenticationConstants.NugetBasicAuthenticationScheme))
            {
                options.AddScheme<NugetBasicAuthenticationHandler>(AuthenticationConstants.NugetBasicAuthenticationScheme, AuthenticationConstants.NugetBasicAuthenticationScheme);
                options.DefaultAuthenticateScheme = AuthenticationConstants.NugetBasicAuthenticationScheme;
                options.DefaultChallengeScheme = AuthenticationConstants.NugetBasicAuthenticationScheme;
            }
        });

        return app;
    }

    internal static ElPaquetoApplication AddNugetBasicHttpAuthorization(this ElPaquetoApplication app, Action<AuthorizationPolicyBuilder>? configurePolicy = null)
    {
        app.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthenticationConstants.NugetUserPolicy, policy =>
            {
                policy.RequireAuthenticatedUser();
                configurePolicy?.Invoke(policy);
            });
        });

        return app;
    }
}
