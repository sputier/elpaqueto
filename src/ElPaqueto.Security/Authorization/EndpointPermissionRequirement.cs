using System.Security.Claims;
using ElPaqueto.Core;
using ElPaqueto.Security.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Authorization;

/// <summary>
/// The single requirement behind the fallback, default and <c>NuGetUserPolicy</c> policies: the
/// permission of an endpoint comes from <see cref="EndpointPermissionTable"/>.
/// </summary>
internal sealed class EndpointPermissionRequirement : IAuthorizationRequirement;

/// <summary>
/// Decides whether the current request may reach its endpoint. It fails closed: no endpoint, no key, no
/// row, a method the row does not allow, an unauthenticated caller, a write without an API key sent in
/// <c>X-NuGet-ApiKey</c> or a missing permission all fail.
/// </summary>
/// <remarks>
/// Permissions are read from the claims built by authentication: this handler never queries a database.
/// Failing for an unauthenticated caller makes the framework challenge (401); failing for an
/// authenticated one makes it forbid (403).
/// </remarks>
internal sealed class EndpointPermissionHandler(IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<EndpointPermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, EndpointPermissionRequirement requirement)
    {
        var httpContext = context.Resource as HttpContext ?? httpContextAccessor.HttpContext;

        if (Decide(context.User, httpContext))
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail();
        }

        return Task.CompletedTask;
    }

    private static bool Decide(ClaimsPrincipal user, HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return false;
        }

        var endpoint = httpContext.GetEndpoint();
        var key = EndpointKey.Resolve(endpoint);
        if (endpoint is null || key is null || !EndpointPermissionTable.TryGetRule(key, out var rule))
        {
            return false;
        }

        // A route name is only a label: an endpoint that reuses it for another action is denied.
        if (!string.Equals(rule.Action, EndpointKey.ActionOf(endpoint), StringComparison.Ordinal))
        {
            return false;
        }

        if (!rule.Methods.Contains(httpContext.Request.Method, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rule.Anonymous)
        {
            return true;
        }

        if (user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        // Writes: only an API key sent in X-NuGet-ApiKey, never a password or a key sent as a Basic password.
        if (rule.RequiresApiKeyHeader && user.ApiKeyIdFromHeader() is null)
        {
            return false;
        }

        var required = rule.Required;
        if (key == EndpointPermissionTable.DeleteKey
            && httpContext.RequestServices.GetRequiredService<IOptionsSnapshot<ElPaquetoOptions>>().Value.PackageDeletionBehavior
                == PackageDeletionBehavior.HardDelete)
        {
            // Deleting for good cannot be undone and can break consumers' restores.
            required |= Permission.Admin;
        }

        return user.GrantedPermissions().HasFlag(required);
    }
}
