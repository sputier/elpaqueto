using System.Security.Claims;
using System.Text.Encodings.Web;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Data;
using ElPaqueto.Security.Passwords;
using ElPaqueto.Security.Throttling;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Authentication;

/// <summary>
/// Authenticates a request with an API key or with HTTP Basic credentials checked against the security
/// database (ADR 0002, decision 6).
/// </summary>
/// <remarks>
/// <para>The credentials are considered in this order, and exactly one of them decides:</para>
/// <list type="number">
/// <item><c>X-NuGet-ApiKey</c> present: the key alone decides and <c>Authorization</c> is ignored.</item>
/// <item><c>Authorization: Basic</c>: a password with the API key token format is validated as a key only;
/// any other password is verified as the user's password only.</item>
/// <item>Anything else: no result.</item>
/// </list>
/// <para>
/// It never produces an authenticated principal for an anonymous request and never combines two credentials.
/// Every failure (malformed header, unknown user, disabled user, wrong password, invalid, revoked or expired
/// key, disabled key owner) ends in the same generic result. The user, or the key and its owner, is read from
/// the database on every request, so a disable, a permission change, a password reset or a key revocation made
/// by the command line applies to the next request.
/// </para>
/// </remarks>
internal sealed partial class ElPaquetoAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    AuthDbContext database,
    IPasswordService passwords,
    IApiKeyService apiKeys,
    VerificationCache verificationCache,
    ILoginThrottle throttle)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    private const string AttemptRefused = "Attempt refused by throttling.";

    /// <summary>The header NuGet clients send the API key in, on push and delete.</summary>
    internal const string ApiKeyHeaderName = RequestCredentialSelector.ApiKeyHeaderName;

    // Fixed strings: the base class logs them, so they must never contain request data.
    private const string InvalidCredentials = "Invalid credentials.";

    private static readonly Permission[] Flags = [Permission.Read, Permission.Publish, Permission.Admin];

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Exactly one credential decides; the selection order is in RequestCredentialSelector. Every branch below
        // either verifies that credential in full or fails: no branch grants anything by itself.
        var credential = RequestCredentialSelector.Select(Request.Headers);

        return credential.Kind switch
        {
            CredentialKind.ApiKey => await AuthenticateApiKeyAsync(credential.Secret, credential.Source!),

            // The parser guarantees a non-empty user name. A token-shaped password never reaches this path, and the
            // password policy refuses passwords with that shape, so no value is ever tried both ways.
            CredentialKind.Password => await AuthenticatePasswordAsync(credential.UserName!, credential.Secret!),
            CredentialKind.Invalid => Refuse(credential),
            _ => AuthenticateResult.NoResult(),
        };
    }

    private AuthenticateResult Refuse(RequestCredential credential)
    {
        if (credential.Source is { } source)
        {
            // Two API key header values.
            LogMalformedApiKeyRefused(Logger, ApiKeyValidationFailure.Malformed, source);
        }

        return AuthenticateResult.Fail(InvalidCredentials);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // A throttled attempt is told when to retry, and is not asked for credentials again (ADR 0002, decision 11).
        if (Context.Features.Get<RefusedAttemptFeature>() is { } refused)
        {
            Response.StatusCode = refused.Rejection.StatusCode;
            Response.Headers.RetryAfter = refused.Rejection.RetryAfterHeaderValue;
            return Task.CompletedTask;
        }

        // A challenge, never a redirect: NuGet clients and scripts must get a plain 401.
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Basic realm=\"ElPaqueto\"";
        return Task.CompletedTask;
    }

    private AuthenticateResult Refuse(ThrottleRejection rejection)
    {
        Context.Features.Set(new RefusedAttemptFeature(rejection));
        return AuthenticateResult.Fail(AttemptRefused);
    }

    // A key has no user name to count against: its attempts are counted against the client (and its IPv6 network)
    // only, whether the key came from the header or from a Basic password. The throttle is consulted before the key
    // is even parsed, so a throttled client gets 429 without any validation or database access. Every failed
    // validation counts; a success resets nothing, since a key attempt has no pair counter to reset.
    private async Task<AuthenticateResult> AuthenticateApiKeyAsync(string? token, string source)
    {
        var client = Context.Connection.RemoteIpAddress;
        if (throttle.Check(client, null) is { } throttled)
        {
            return Refuse(throttled);
        }

        // The slot reports the outcome and keeps a burst of parallel guesses from one client within its budget.
        using var slot = await throttle.EnterVerificationAsync(client, null, Context.RequestAborted);
        if (slot.Rejection is { } refused)
        {
            return Refuse(refused);
        }

        var result = await apiKeys.ValidateAsync(token, Context.RequestAborted);
        if (!result.Succeeded)
        {
            slot.Failed();

            // The reason and the public key id only: never the token. A value without the token format is logged at
            // Debug only, so that anonymous garbage cannot fill the logs; a well-formed key that is refused is worth
            // an Information line.
            if (result.KeyId is null)
            {
                LogMalformedApiKeyRefused(Logger, result.Failure, source);
            }
            else
            {
                LogApiKeyRefused(Logger, result.Failure, result.KeyId, source);
            }

            return AuthenticateResult.Fail(InvalidCredentials);
        }

        slot.Succeeded();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.OwnerUserId.ToString("D")),
            new(ClaimTypes.Name, result.OwnerUserName!),
            new(ElPaquetoClaimTypes.AuthenticationMethod, ElPaquetoClaimTypes.ApiKeyMethod),
            new(ElPaquetoClaimTypes.ApiKeyId, result.KeyId!),
            new(ElPaquetoClaimTypes.CredentialSource, source),
        };

        // The effective permissions: the key's, intersected with the owner's current ones.
        AddPermissionClaims(claims, result.EffectivePermissions);
        return Succeed(claims);
    }

    private async Task<AuthenticateResult> AuthenticatePasswordAsync(string userName, string password)
    {
        var client = Context.Connection.RemoteIpAddress;

        // Before any lookup or verification: a throttled attempt costs nothing and learns nothing.
        if (throttle.Check(client, userName) is { } throttled)
        {
            return Refuse(throttled);
        }

        User? user = null;
        if (UserNames.IsValid(userName))
        {
            var normalized = User.Normalize(userName);
            user = await database.Users.SingleOrDefaultAsync(u => u.NormalizedUserName == normalized, Context.RequestAborted);
        }

        // Unknown and disabled users are verified against a dummy hash inside the password service.
        if (user is { IsActive: true } && verificationCache.Contains(user, password))
        {
            throttle.RecordSuccess(client, userName);
            return SucceedWithPassword(user);
        }

        using var slot = await throttle.EnterVerificationAsync(client, userName, Context.RequestAborted);
        if (slot.Rejection is { } refused)
        {
            return Refuse(refused);
        }

        // While this attempt waited, another one for the same user name and client may have succeeded.
        if (user is { IsActive: true } && verificationCache.Contains(user, password))
        {
            slot.Succeeded();
            return SucceedWithPassword(user);
        }

        switch (passwords.Verify(user, password))
        {
            case PasswordVerification.Success:
                verificationCache.Remember(user!, password);
                slot.Succeeded();
                return SucceedWithPassword(user!);

            case PasswordVerification.SuccessRehashNeeded:
                await UpgradeHashAsync(user!, password);
                verificationCache.Remember(user!, password);
                slot.Succeeded();
                return SucceedWithPassword(user!);

            default:
                slot.Failed();
                return AuthenticateResult.Fail(InvalidCredentials);
        }
    }

    private AuthenticateResult SucceedWithPassword(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
            new(ClaimTypes.Name, user.UserName),
            new(ElPaquetoClaimTypes.AuthenticationMethod, ElPaquetoClaimTypes.PasswordMethod),
        };

        AddPermissionClaims(claims, user.Permissions);
        return Succeed(claims);
    }

    private static void AddPermissionClaims(List<Claim> claims, Permission permissions)
    {
        foreach (var permission in Flags)
        {
            if (permissions.HasFlag(permission))
            {
                claims.Add(new Claim(ElPaquetoClaimTypes.Permission, permission.ToString()));
            }
        }
    }

    private AuthenticateResult Succeed(List<Claim> claims)
    {
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    // The stored hash uses a lower cost or an older format than the configured one: store a fresh hash.
    // A failure here (for example a concurrent update) is logged and never fails the request.
    private async Task UpgradeHashAsync(User user, string password)
    {
        try
        {
            user.PasswordHash = passwords.Hash(password);
            user.UpdatedAt = DateTime.UtcNow;
            await database.SaveChangesAsync(Context.RequestAborted);
        }
        catch (DbUpdateException)
        {
            LogRehashNotStored(Logger, user.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The upgraded password hash of user {UserId} could not be stored; it will be retried at the next login.")]
    private static partial void LogRehashNotStored(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "API key authentication failed: {Reason} (key id {KeyId}, sent as {CredentialSource}).")]
    private static partial void LogApiKeyRefused(ILogger logger, ApiKeyValidationFailure reason, string keyId, string credentialSource);

    [LoggerMessage(Level = LogLevel.Debug, Message = "API key authentication failed: {Reason} (no key id, sent as {CredentialSource}).")]
    private static partial void LogMalformedApiKeyRefused(ILogger logger, ApiKeyValidationFailure reason, string credentialSource);

    private sealed record RefusedAttemptFeature(ThrottleRejection Rejection);
}
