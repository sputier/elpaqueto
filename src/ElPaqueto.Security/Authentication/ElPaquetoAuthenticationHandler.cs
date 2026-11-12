using System.Security.Claims;
using System.Text.Encodings.Web;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Data;
using ElPaqueto.Security.Passwords;
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
    VerificationCache verificationCache)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    /// <summary>The header NuGet clients send the API key in, on push and delete.</summary>
    internal const string ApiKeyHeaderName = "X-NuGet-ApiKey";

    // Fixed strings: the base class logs them, so they must never contain request data.
    private const string InvalidCredentials = "Invalid credentials.";

    private static readonly Permission[] Flags = [Permission.Read, Permission.Publish, Permission.Admin];

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 1. The API key header, when present, is the only credential considered.
        var apiKeyValues = Request.Headers[ApiKeyHeaderName];
        if (apiKeyValues.Count > 0)
        {
            // A client sends one key; several are ambiguous, hence a failure rather than a pick.
            if (apiKeyValues.Count > 1)
            {
                LogMalformedApiKeyRefused(Logger, ApiKeyValidationFailure.Malformed, ElPaquetoClaimTypes.ApiKeyHeaderSource);
                return AuthenticateResult.Fail(InvalidCredentials);
            }

            return await AuthenticateApiKeyAsync(apiKeyValues[0], ElPaquetoClaimTypes.ApiKeyHeaderSource);
        }

        // 2. HTTP Basic.
        var parsed = BasicCredentialsParser.Parse(Request.Headers.Authorization);

        switch (parsed.Outcome)
        {
            case BasicParseOutcome.NoResult:
                return AuthenticateResult.NoResult();
            case BasicParseOutcome.Malformed:
                return AuthenticateResult.Fail(InvalidCredentials);
        }

        // The parser guarantees a non-empty user name. For a key it is otherwise ignored: the key names its owner.
        // A token-shaped password is never verified as a user password, and the password policy refuses
        // passwords with that shape, so no value is ever tried both ways.
        if (ApiKeyToken.IsTokenFormat(parsed.Password))
        {
            return await AuthenticateApiKeyAsync(parsed.Password, ElPaquetoClaimTypes.BasicPasswordSource);
        }

        return await AuthenticatePasswordAsync(parsed.UserName!, parsed.Password!);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // A challenge, never a redirect: NuGet clients and scripts must get a plain 401.
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Basic realm=\"ElPaqueto\"";
        return Task.CompletedTask;
    }

    // Throttling of failed attempts (issue #18) belongs here and in AuthenticatePasswordAsync: consulted before
    // ValidateAsync, keyed by client address and key id, and told about each failure returned below.
    private async Task<AuthenticateResult> AuthenticateApiKeyAsync(string? token, string source)
    {
        var result = await apiKeys.ValidateAsync(token, Context.RequestAborted);
        if (!result.Succeeded)
        {
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
        User? user = null;
        if (UserNames.IsValid(userName))
        {
            var normalized = User.Normalize(userName);
            user = await database.Users.SingleOrDefaultAsync(u => u.NormalizedUserName == normalized, Context.RequestAborted);
        }

        // Unknown and disabled users are verified against a dummy hash inside the password service.
        if (user is { IsActive: true } && verificationCache.Contains(user, password))
        {
            return SucceedWithPassword(user);
        }

        switch (passwords.Verify(user, password))
        {
            case PasswordVerification.Success:
                verificationCache.Remember(user!, password);
                return SucceedWithPassword(user!);

            case PasswordVerification.SuccessRehashNeeded:
                await UpgradeHashAsync(user!, password);
                verificationCache.Remember(user!, password);
                return SucceedWithPassword(user!);

            default:
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
}
