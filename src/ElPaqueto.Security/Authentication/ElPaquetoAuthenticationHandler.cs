using System.Diagnostics;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Auditing;
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
    VerificationTiming verificationTiming,
    ILoginThrottle throttle,
    ILogger<Audit> audit)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    private const string AttemptRefused = "Attempt refused by throttling.";

    /// <summary>The header NuGet clients send the API key in, on push and delete.</summary>
    internal const string ApiKeyHeaderName = RequestCredentialSelector.ApiKeyHeaderName;

    // Fixed strings: the base class logs them, so they must never contain request data.
    private const string InvalidCredentials = "Invalid credentials.";

    private static readonly Permission[] Flags = [Permission.Read, Permission.Publish, Permission.Admin];

    // The credential state of every unknown or disabled user name (CredentialAttempt): one value for all of them, as
    // long as a stored hash, so that their attempts are fingerprinted like any other.
    private static readonly string NoCredentialState = new('-', 84);

    // The audit trail (docs/technical/audit-logging.md): reasons, ids and the client address, never a credential.
    private readonly ILogger<Audit> _audit = audit;

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

    // Malformed credentials are refused before throttling, so they are logged at Debug only: anyone can send them.
    private AuthenticateResult Refuse(RequestCredential credential)
    {
        if (!_audit.IsEnabled(LogLevel.Debug))
        {
            return AuthenticateResult.Fail(InvalidCredentials);
        }

        var client = ClientAddress;
        if (credential.Source is { } source)
        {
            // Two API key header values.
            var reason = AuditValues.Reason(ApiKeyValidationFailure.Malformed);
            AuditLog.ApiKeyRejected(_audit, LogLevel.Debug, reason, AuditValues.None, source, client);
        }
        else
        {
            AuditLog.AuthenticationFailed(
                _audit, LogLevel.Debug, AuditValues.PasswordMethod, AuthenticationFailureReason.Malformed, AuditValues.None, client);
        }

        return AuthenticateResult.Fail(InvalidCredentials);
    }

    // Debug, and checked first: this runs for nearly every request.
    private void LogSucceeded(Guid userId, string method, string keyId)
    {
        if (_audit.IsEnabled(LogLevel.Debug))
        {
            AuditLog.AuthenticationSucceeded(_audit, userId, method, keyId, ClientAddress);
        }
    }

    // After trusted forwarded-header processing: the address of the client, not of the reverse proxy.
    private string ClientAddress => AuditValues.Client(Context.Connection.RemoteIpAddress);

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

    private AuthenticateResult Refuse(ThrottleRejection rejection, string method)
    {
        if (_audit.IsEnabled(LogLevel.Debug))
        {
            AuditLog.AuthenticationThrottled(_audit, method, rejection.StatusCode, ClientAddress, rejection.RetryAfterHeaderValue);
        }
        Context.Features.Set(new RefusedAttemptFeature(rejection));
        return AuthenticateResult.Fail(AttemptRefused);
    }

    // A key has no user name to count against: its attempts are counted against the client (and its IPv6 network)
    // only, whether the key came from the header or from a Basic password. The throttle is consulted before the key
    // is even parsed, so a throttled client gets 429 without any validation or database access. Every failed
    // validation counts, once per identical token and window; a success resets nothing, since a key attempt has no
    // pair counter to reset. A repeated key is still validated: it is cheap, and the key or its owner may have
    // changed since.
    private async Task<AuthenticateResult> AuthenticateApiKeyAsync(string? token, string source)
    {
        var client = Context.Connection.RemoteIpAddress;
        if (throttle.Check(client, null) is { } throttled)
        {
            return Refuse(throttled, AuditValues.ApiKeyMethod);
        }

        // The slot reports the outcome and keeps a burst of parallel guesses from one client within its budget.
        using var slot = await throttle.EnterVerificationAsync(
            client, null, new CredentialAttempt(token ?? string.Empty, string.Empty), Context.RequestAborted);
        if (slot.Rejection is { } refused)
        {
            return Refuse(refused, AuditValues.ApiKeyMethod);
        }

        var result = await apiKeys.ValidateAsync(token, Context.RequestAborted);
        if (!result.Succeeded)
        {
            var counted = slot.Failed();

            // The reason and the public key id only: never the token. A value without the token format is logged at
            // Debug only, so that anonymous garbage cannot fill the logs; a well-formed key that is refused is worth
            // a warning (key attempts are throttled, so their number is bounded), once: its repetitions, which the
            // throttle does not count, are logged at Debug.
            var reason = AuditValues.Reason(result.Failure);
            AuditLog.ApiKeyRejected(
                _audit,
                result.KeyId is null || !counted ? LogLevel.Debug : LogLevel.Warning,
                reason,
                result.KeyId ?? AuditValues.None,
                source,
                ClientAddress);

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
        LogSucceeded(result.OwnerUserId, AuditValues.ApiKeyMethod, result.KeyId!);
        return Succeed(claims);
    }

    private async Task<AuthenticateResult> AuthenticatePasswordAsync(string userName, string password)
    {
        var client = Context.Connection.RemoteIpAddress;

        // Before any lookup or verification: a throttled attempt costs nothing and learns nothing.
        if (throttle.Check(client, userName) is { } throttled)
        {
            return Refuse(throttled, AuditValues.PasswordMethod);
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

        // What decides the verification besides the password: the stored hash of an active user, else one value
        // for every unknown or disabled user name, which keeps them indistinguishable here too.
        var state = user is { IsActive: true, PasswordHash.Length: > 0 } ? user.PasswordHash : NoCredentialState;
        using var slot = await throttle.EnterVerificationAsync(
            client, userName, new CredentialAttempt(password, state), Context.RequestAborted);
        if (slot.Rejection is { } refused)
        {
            return Refuse(refused, AuditValues.PasswordMethod);
        }

        // While this attempt waited, another one for the same user name and client may have succeeded.
        if (user is { IsActive: true } && verificationCache.Contains(user, password))
        {
            slot.Succeeded();
            return SucceedWithPassword(user);
        }

        // The same password, for the same account state, already failed from this client: verifying it again
        // would fail again. Answered without a hash, so that a client retrying a stale password costs no processor
        // time, after the time a verification takes.
        if (slot.RepeatsACountedFailure)
        {
            var counted = slot.Failed();
            LogPasswordFailure(user, userName, counted);
            await Task.Delay(verificationTiming.Typical, Context.RequestAborted).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return AuthenticateResult.Fail(InvalidCredentials);
        }

        var started = Stopwatch.GetTimestamp();
        var verification = passwords.Verify(user, password);
        verificationTiming.Record(started);

        switch (verification)
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
                LogPasswordFailure(user, userName, slot.Failed());
                return AuthenticateResult.Fail(InvalidCredentials);
        }
    }

    // The reason category, and the user name only when it names an existing account: a name that matches no account may
    // be a password or an API key typed in the wrong field, or carry line breaks, and is logged as a fingerprint.
    // A repetition of a failure already counted is logged at Debug: the throttle does not limit repetitions, so
    // logging them as warnings would let anyone fill the log.
    private void LogPasswordFailure(User? user, string userName, bool counted)
    {
        var level = counted ? LogLevel.Warning : LogLevel.Debug;
        if (!_audit.IsEnabled(level))
        {
            return;
        }

        var reason = user switch
        {
            null => AuthenticationFailureReason.UnknownUser,
            { IsActive: false } => AuthenticationFailureReason.Disabled,
            _ => AuthenticationFailureReason.BadPassword,
        };

        var loggedName = user is null ? AuditValues.UnknownUserName(userName) : AuditValues.UserName(user.UserName);
        AuditLog.AuthenticationFailed(_audit, level, AuditValues.PasswordMethod, reason, loggedName, ClientAddress);
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
        LogSucceeded(user.Id, AuditValues.PasswordMethod, AuditValues.None);
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

    [LoggerMessage(EventId = 3001, EventName = "PasswordRehashNotStored", Level = LogLevel.Warning,
        Message = "The upgraded password hash of user {UserId} could not be stored; it will be retried at the next login.")]
    private static partial void LogRehashNotStored(ILogger logger, Guid userId);

    private sealed record RefusedAttemptFeature(ThrottleRejection Rejection);
}
