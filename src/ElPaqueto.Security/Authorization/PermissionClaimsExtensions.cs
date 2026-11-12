using System.Security.Claims;
using ElPaqueto.Security.Authentication;

namespace ElPaqueto.Security.Authorization;

/// <summary>
/// Reads permissions from the claims built by authentication.
/// </summary>
public static class PermissionClaimsExtensions
{
    /// <summary>
    /// Returns every permission flag carried by the principal's permission claims. An unauthenticated
    /// principal has none, whatever claims it carries.
    /// </summary>
    /// <param name="user">The principal.</param>
    /// <returns>The granted flags, <see cref="Permission.None"/> when there are none.</returns>
    public static Permission GrantedPermissions(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.Identity?.IsAuthenticated != true)
        {
            return Permission.None;
        }

        var granted = Permission.None;
        foreach (var claim in user.FindAll(ElPaquetoClaimTypes.Permission))
        {
            // Only the three defined names count: an unknown value grants nothing.
            if (Enum.TryParse<Permission>(claim.Value, ignoreCase: false, out var flag)
                && Enum.IsDefined(flag)
                && flag != Permission.None)
            {
                granted |= flag;
            }
        }

        return granted;
    }

    /// <summary>
    /// Whether the principal holds every flag of <paramref name="permission"/>.
    /// </summary>
    /// <param name="user">The principal.</param>
    /// <param name="permission">The required flags. <see cref="Permission.None"/> is always held by an authenticated principal.</param>
    /// <returns><see langword="true"/> when all the flags are granted.</returns>
    public static bool HasPermission(this ClaimsPrincipal user, Permission permission)
        => user.Identity?.IsAuthenticated == true && user.GrantedPermissions().HasFlag(permission);
}
