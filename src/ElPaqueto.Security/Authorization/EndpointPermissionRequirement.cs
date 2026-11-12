using System.Security.Claims;
using ElPaqueto.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Authorization;

/// <summary>
/// The single requirement behind the fallback, default and <c>NuGetUserPolicy</c> policies: the
/// permission of an endpoint comes from <see cref="EndpointPermissionTable"/>.
/// </summary>
internal sealed class EndpointPermissionRequirement : IAuthorizationRequirement;

/// <summary>
/// Decides whether the current request may reach its endpoint. It fails closed: no endpoint, no key, no
/// row, a method the row does not allow, an unauthenticated caller or a missing permission all fail.
/// </summary>
/// <remarks>
/// Permissions are read from the claims built by authentication: this handler never queries a database.
/// Failing for an unauthenticated caller makes the framework challenge (401); failing for an
/// authenticated one makes it forbid (403).
/// </remarks>
internal sealed class EndpointPermissionHandler(
    IHttpContextAccessor httpContextAccessor,
    IOptionsMonitor<ElPaquetoOptions> options) : AuthorizationHandler<EndpointPermissionRequirement>
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

    private bool Decide(ClaimsPrincipal user, HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return false;
        }

        var key = EndpointKey.Resolve(httpContext.GetEndpoint());
        if (key is null || !EndpointPermissionTable.TryGetRule(key, out var rule))
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

        var required = rule.Required;
        if (key == EndpointPermissionTable.DeleteKey
            && options.CurrentValue.PackageDeletionBehavior == PackageDeletionBehavior.HardDelete)
        {
            // Deleting for good cannot be undone and can break consumers' restores.
            required |= Permission.Admin;
        }

        return user.GrantedPermissions().HasFlag(required);
    }
}
