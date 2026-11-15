using ElPaqueto.Core;
using ElPaqueto.Core.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using HealthCheckOptions = Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions;

namespace ElPaqueto.Security.Authorization;

/// <summary>
/// Maps the health endpoints as real endpoints, so that the authorization policies apply to them.
/// </summary>
/// <remarks>
/// The inherited code ran the health check as terminal middleware after the endpoints, where endpoint
/// authorization cannot see it, and printed the configured services to anonymous callers.
/// </remarks>
public static class HealthEndpoints
{
    /// <summary>The liveness path used when the configuration has no <c>HealthCheck</c> section.</summary>
    public const string DefaultPath = "/health";

    /// <summary>
    /// Maps the liveness endpoint at the configured path and the detailed one below it.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="options">The ElPaqueto options, for the path, the status property name and the configured checks.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// The liveness endpoint is anonymous and answers with the overall status only: container
    /// orchestrators need it. <c>/details</c> requires the Admin permission and adds the status of each check.
    /// Without a <c>HealthCheck</c> section, the path is <see cref="DefaultPath"/>.
    /// </remarks>
    public static IEndpointRouteBuilder MapElPaquetoHealthChecks(this IEndpointRouteBuilder endpoints, ElPaquetoOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        // Without a HealthCheck section the binder leaves the property null: use the shipped defaults.
        var healthCheck = options.HealthCheck ?? new Core.HealthCheckOptions { Path = DefaultPath };
        var path = healthCheck.Path?.TrimEnd('/');
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException("HealthCheck:Path must name a path below the root, such as /health: the root belongs to the home page.");
        }

        endpoints
            .MapHealthChecks(path, Create(options, healthCheck.StatusPropertyName, detailed: false))
            .WithName(EndpointPermissionTable.HealthKey);

        endpoints
            .MapHealthChecks(path + "/details", Create(options, healthCheck.StatusPropertyName, detailed: true))
            .WithName(EndpointPermissionTable.HealthDetailsKey);

        return endpoints;
    }

    private static HealthCheckOptions Create(ElPaquetoOptions options, string statusPropertyName, bool detailed) => new()
    {
        ResponseWriter = async (context, report) =>
        {
            await report.FormatAsJson(
                context.Response.Body,
                detailed,
                statusPropertyName,
                context.RequestAborted);
        },
        Predicate = check => check.IsConfigured(options),
    };
}
