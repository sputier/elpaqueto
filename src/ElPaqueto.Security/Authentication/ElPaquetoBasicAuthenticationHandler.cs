using System.Security.Claims;
using System.Text.Encodings.Web;
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
/// Authenticates users with HTTP Basic credentials checked against the security database.
/// </summary>
/// <remarks>
/// It never produces an authenticated principal for an anonymous request. Every failure
/// (malformed header, unknown user, disabled user, wrong password) ends in the same generic result,
/// and the unknown and disabled paths do as much password-hashing work as a real verification.
/// The user is read from the database on every request, so a disable, a permission change or a
/// password reset made by the command line applies to the next request.
/// </remarks>
internal sealed partial class ElPaquetoBasicAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    AuthDbContext database,
    IPasswordService passwords,
    VerificationCache verificationCache,
    ILoginThrottle throttle)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    // Fixed strings: the base class logs them, so they must never contain request data.
    private const string InvalidCredentials = "Invalid credentials.";
    private const string AttemptRefused = "Attempt refused by throttling.";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var parsed = BasicCredentialsParser.Parse(Request.Headers.Authorization);

        switch (parsed.Outcome)
        {
            case BasicParseOutcome.NoResult:
                return AuthenticateResult.NoResult();
            case BasicParseOutcome.Malformed:
                return AuthenticateResult.Fail(InvalidCredentials);
        }

        var userName = parsed.UserName!;
        var password = parsed.Password!;
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
            return Succeed(user);
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
            return Succeed(user);
        }

        switch (passwords.Verify(user, password))
        {
            case PasswordVerification.Success:
                verificationCache.Remember(user!, password);
                slot.Succeeded();
                return Succeed(user!);

            case PasswordVerification.SuccessRehashNeeded:
                await UpgradeHashAsync(user!, password);
                verificationCache.Remember(user!, password);
                slot.Succeeded();
                return Succeed(user!);

            default:
                slot.Failed();
                return AuthenticateResult.Fail(InvalidCredentials);
        }
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

    private AuthenticateResult Succeed(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
            new(ClaimTypes.Name, user.UserName),
            new(ElPaquetoClaimTypes.AuthenticationMethod, ElPaquetoClaimTypes.PasswordMethod),
        };

        foreach (var permission in new[] { Permission.Read, Permission.Publish, Permission.Admin })
        {
            if (user.Permissions.HasFlag(permission))
            {
                claims.Add(new Claim(ElPaquetoClaimTypes.Permission, permission.ToString()));
            }
        }

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

    private sealed record RefusedAttemptFeature(ThrottleRejection Rejection);
}
