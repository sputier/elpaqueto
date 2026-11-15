using System.Security.Claims;

namespace ElPaqueto.Security.Authentication;

/// <summary>
/// Reads, from the claims built by authentication, which credential authenticated the request.
/// </summary>
public static class CredentialClaimsExtensions
{
    /// <summary>
    /// Returns the public key id of the API key that authenticated the request when, and only when, the key
    /// was sent in the <c>X-NuGet-ApiKey</c> header: the credential write routes demand.
    /// </summary>
    /// <param name="user">The principal.</param>
    /// <returns>
    /// The key id, or <see langword="null"/> for an unauthenticated principal, a password, a key sent as a
    /// Basic password, or a principal whose claims are ambiguous (several authenticated identities, or a
    /// claim present more than once).
    /// </returns>
    public static string? ApiKeyIdFromHeader(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        // The handler builds exactly one identity; a principal combining several is never trusted for a write.
        ClaimsIdentity? identity = null;
        foreach (var candidate in user.Identities)
        {
            if (!candidate.IsAuthenticated)
            {
                continue;
            }

            if (identity is not null)
            {
                return null;
            }

            identity = candidate;
        }

        if (identity is null
            || SingleValue(identity, ElPaquetoClaimTypes.AuthenticationMethod) != ElPaquetoClaimTypes.ApiKeyMethod
            || SingleValue(identity, ElPaquetoClaimTypes.CredentialSource) != ElPaquetoClaimTypes.ApiKeyHeaderSource)
        {
            return null;
        }

        return SingleValue(identity, ElPaquetoClaimTypes.ApiKeyId);
    }

    private static string? SingleValue(ClaimsIdentity identity, string type)
    {
        string? value = null;
        foreach (var claim in identity.FindAll(type))
        {
            if (value is not null)
            {
                return null;
            }

            value = claim.Value;
        }

        return string.IsNullOrEmpty(value) ? null : value;
    }
}
