using System.Security.Claims;
using ElPaqueto.Core;
using ElPaqueto.Security.Auditing;
using ElPaqueto.Security.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// authenticated one makes it forbid (403), and is logged to the audit trail. Unauthenticated failures are not
/// logged: every NuGet client starts with an anonymous request.
/// </remarks>
internal sealed class EndpointPermissionHandler(IHttpContextAccessor httpContextAccessor, ILogger<Audit> audit)
    : AuthorizationHandler<EndpointPermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, EndpointPermissionRequirement requirement)
    {
        var httpContext = context.Resource as HttpContext ?? httpContextAccessor.HttpContext;
        var decision = Decide(context.User, httpContext);

        if (decision.Allowed)
        {
            context.Succeed(requirement);
        }
        else
        {
            if (httpContext is not null && context.User.Identity?.IsAuthenticated == true)
            {
                LogDenied(context.User, httpContext, decision);
            }

            context.Fail();
        }

        return Task.CompletedTask;
    }

    private void LogDenied(ClaimsPrincipal user, HttpContext httpContext, Decision decision)
    {
        AuditLog.AuthorizationDenied(
            audit,
            user.FindFirstValue(ClaimTypes.NameIdentifier) ?? AuditValues.None,
            user.FindFirstValue(ElPaquetoClaimTypes.AuthenticationMethod) ?? AuditValues.None,
            user.FindFirstValue(ElPaquetoClaimTypes.ApiKeyId) ?? AuditValues.None,
            AuditValues.HttpMethod(httpContext.Request.Method),
            decision.Endpoint ?? AuditValues.None,
            decision.Reason!,
            decision.Required,
            AuditValues.Client(httpContext.Connection.RemoteIpAddress));
    }

    private static Decision Decide(ClaimsPrincipal user, HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return Decision.Deny(null, AuthorizationDenialReason.UnmappedEndpoint);
        }

        var endpoint = httpContext.GetEndpoint();
        var key = EndpointKey.Resolve(endpoint);
        if (endpoint is null || key is null || !EndpointPermissionTable.TryGetRule(key, out var rule))
        {
            return Decision.Deny(key, AuthorizationDenialReason.UnmappedEndpoint);
        }

        // A route name is only a label: an endpoint that reuses it for another action is denied.
        if (!string.Equals(rule.Action, EndpointKey.ActionOf(endpoint), StringComparison.Ordinal))
        {
            return Decision.Deny(key, AuthorizationDenialReason.RouteMismatch);
        }

        if (!rule.Methods.Contains(httpContext.Request.Method, StringComparer.OrdinalIgnoreCase))
        {
            return Decision.Deny(key, AuthorizationDenialReason.MethodNotAllowed, rule.Required);
        }

        if (rule.Anonymous)
        {
            return Decision.Allow;
        }

        if (user.Identity?.IsAuthenticated != true)
        {
            return Decision.Deny(key, AuthorizationDenialReason.MissingPermission, rule.Required);
        }

        // Writes: only an API key sent in X-NuGet-ApiKey, never a password or a key sent as a Basic password.
        if (rule.RequiresApiKeyHeader && user.ApiKeyIdFromHeader() is null)
        {
            return Decision.Deny(key, AuthorizationDenialReason.ApiKeyHeaderRequired, rule.Required);
        }

        var required = rule.Required;
        if (key == EndpointPermissionTable.DeleteKey
            && httpContext.RequestServices.GetRequiredService<IOptionsSnapshot<ElPaquetoOptions>>().Value.PackageDeletionBehavior
                == PackageDeletionBehavior.HardDelete)
        {
            // Deleting for good cannot be undone and can break consumers' restores.
            required |= Permission.Admin;
        }

        return user.GrantedPermissions().HasFlag(required)
            ? Decision.Allow
            : Decision.Deny(key, AuthorizationDenialReason.MissingPermission, required);
    }

    /// <summary>The outcome of <see cref="Decide"/>; for a denial, what the audit trail records.</summary>
    private readonly record struct Decision(bool Allowed, string? Endpoint, string? Reason, Permission Required)
    {
        public static Decision Allow => new(true, null, null, Permission.None);

        public static Decision Deny(string? endpoint, string reason, Permission required = Permission.None)
            => new(false, endpoint, reason, required);
    }
}
