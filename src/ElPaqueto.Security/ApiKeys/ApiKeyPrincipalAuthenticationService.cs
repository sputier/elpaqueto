using ElPaqueto.Core;
using ElPaqueto.Security.Authentication;
using ElPaqueto.Security.Authorization;
using Microsoft.AspNetCore.Http;

namespace ElPaqueto.Security.ApiKeys;

/// <summary>
/// Replaces the inherited <c>ApiKeyAuthenticationService</c> behind the inline check that the publish, delete,
/// relist and symbol upload actions still perform (ADR 0002, decision 7).
/// </summary>
/// <remarks>
/// <para>
/// It decides from the principal that authentication already built for the current request and never
/// queries the database: it returns <see langword="true"/> only if that principal was authenticated by an API
/// key sent in the <c>X-NuGet-ApiKey</c> header, the key id of the value passed in is that key's, and
/// the principal holds <see cref="Permission.Publish"/>.
/// </para>
/// <para>
/// The endpoint policy has already demanded exactly this before the action runs, so for an authorized write
/// the answer is always <see langword="true"/>: this is defence in depth only, not the full rule (a hard delete,
/// for example, also needs <see cref="Permission.Admin"/>, which only the endpoint policy checks). The legacy <c>ApiKey</c> and
/// <c>Authentication:ApiKeys</c> settings play no part, and no configuration opens publishing to everyone.
/// </para>
/// </remarks>
internal sealed class ApiKeyPrincipalAuthenticationService(IHttpContextAccessor httpContextAccessor) : IAuthenticationService
{
    public Task<bool> AuthenticateAsync(string apiKey, CancellationToken cancellationToken)
        => Task.FromResult(Authenticate(apiKey));

    private bool Authenticate(string? apiKey)
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user is null || !ApiKeyToken.TryParse(apiKey, out var presented))
        {
            return false;
        }

        var keyId = user.ApiKeyIdFromHeader();

        // Key ids are public: an ordinary comparison reveals nothing. The secret was proven by authentication.
        return keyId is not null
            && string.Equals(keyId, presented.KeyId, StringComparison.Ordinal)
            && user.HasPermission(Permission.Publish);
    }
}
