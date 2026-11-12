using ElPaqueto.Security.Throttling;
using Microsoft.Extensions.Logging;

namespace ElPaqueto.Security.Auditing;

/// <summary>
/// The security audit events: sign-ins, API key refusals, authorization denials, transport refusals, throttling
/// and startup security warnings. They are logged under the <see cref="Category"/>
/// category (inject <c>ILogger&lt;Audit&gt;</c>). The user and API key administration events are in
/// <see cref="Users.UserAdministrationLog"/>, under the same category.
/// </summary>
/// <remarks>
/// <para>
/// Event ids and names are stable: operators build alerts on them. The allocation, by block of one hundred, and
/// every property are documented in <c>docs/technical/audit-logging.md</c>; a test fails when two events of the
/// security module share an id.
/// </para>
/// <para>
/// Properties are ids, fixed vocabularies, client addresses and user names that follow the user name rules
/// (<see cref="AuditValues.UserName"/>). Never a password, a token, a header value or a connection string.
/// </para>
/// </remarks>
internal static partial class AuditLog
{
    /// <summary>The logging category of the audit trail.</summary>
    public const string Category = "ElPaqueto.Security.Audit";

    // ---- 1000-1099: authentication ----

    /// <summary>A request was authenticated. Debug: NuGet clients authenticate every request.</summary>
    [LoggerMessage(EventId = 1001, EventName = "AuthenticationSucceeded", Level = LogLevel.Debug,
        Message = "User {UserId} authenticated with {Method} (key id {KeyId}) from {ClientAddress}.")]
    public static partial void AuthenticationSucceeded(ILogger logger, Guid userId, string method, string keyId, string clientAddress);

    /// <summary>
    /// A password sign-in failed. Warning, except a malformed header (Debug): malformed headers are refused before
    /// throttling, so anyone could otherwise fill the log with them.
    /// </summary>
    [LoggerMessage(EventId = 1002, EventName = "AuthenticationFailed",
        Message = "Authentication with {Method} failed: {Reason} (user {UserName}) from {ClientAddress}.")]
    public static partial void AuthenticationFailed(ILogger logger, LogLevel level, string method, string reason, string userName, string clientAddress);

    /// <summary>
    /// An API key was refused. Warning when the value has the token format (its key id is logged), Debug otherwise.
    /// </summary>
    [LoggerMessage(EventId = 1003, EventName = "ApiKeyRejected",
        Message = "API key refused: {Reason} (key id {KeyId}, sent as {CredentialSource}) from {ClientAddress}.")]
    public static partial void ApiKeyRejected(ILogger logger, LogLevel level, string reason, string keyId, string credentialSource, string clientAddress);

    /// <summary>
    /// An attempt was refused by throttling (429) or for lack of verification capacity (503), without checking
    /// its credential. Debug: one per refused request; the trip itself is <see cref="ThrottleTripped"/>.
    /// </summary>
    [LoggerMessage(EventId = 1004, EventName = "AuthenticationThrottled", Level = LogLevel.Debug,
        Message = "Authentication with {Method} refused with status {StatusCode} from {ClientAddress}; retry after {RetryAfterSeconds} seconds.")]
    public static partial void AuthenticationThrottled(ILogger logger, string method, int statusCode, string clientAddress, string retryAfterSeconds);

    // ---- 1100-1199: authorization ----

    /// <summary>An authenticated caller was denied an endpoint (403). Anonymous callers are challenged, not logged.</summary>
    [LoggerMessage(EventId = 1101, EventName = "AuthorizationDenied", Level = LogLevel.Warning,
        Message = "User {UserId} ({Method}, key id {KeyId}) denied {HttpMethod} {Endpoint}: {Reason} (requires {RequiredPermission}) from {ClientAddress}.")]
    public static partial void AuthorizationDenied(
        ILogger logger,
        string userId,
        string method,
        string keyId,
        string httpMethod,
        string endpoint,
        string reason,
        Permission requiredPermission,
        string clientAddress);

    // ---- 1200-1299: transport ----

    /// <summary>
    /// A remote plain-HTTP request was refused (403) while HTTPS is required. Warning at most once a minute, Debug
    /// otherwise: these requests are anonymous and refused before throttling.
    /// </summary>
    [LoggerMessage(EventId = 1201, EventName = "TransportRejected",
        Message = "Refused a remote {Scheme} request from {ClientAddress}: HTTPS is required.")]
    public static partial void TransportRejected(ILogger logger, LogLevel level, string scheme, string clientAddress);

    /// <summary>Forwarded headers from an untrusted sender were ignored. Logged once per process.</summary>
    [LoggerMessage(EventId = 1202, EventName = "UntrustedForwardedHeaders", Level = LogLevel.Warning,
        Message = "A plain-HTTP request from {ClientAddress} carried forwarded headers that were not applied: "
            + "the sender is not a trusted proxy, or the proxy chain is longer than Security:ReverseProxy:ForwardLimit. "
            + "If it is your reverse proxy, add it to Security:ReverseProxy:KnownProxies or Security:ReverseProxy:KnownNetworks. "
            + "Reported once.")]
    public static partial void UntrustedForwardedHeaders(ILogger logger, string clientAddress);

    // ---- 1800-1899: throttling ----

    /// <summary>A failure counter tripped. Logged once per trip, not per refused attempt.</summary>
    [LoggerMessage(EventId = 1801, EventName = "ThrottleTripped", Level = LogLevel.Warning,
        Message = "Sign-in throttling tripped on the {Scope} counter for client {Client}; attempts are refused for {Seconds} seconds.")]
    public static partial void ThrottleTripped(ILogger logger, ThrottleScope scope, string client, long seconds);

    // ---- 1900-1999: startup security warnings ----

    /// <summary><c>Security:Transport:RequireHttps</c> is false.</summary>
    [LoggerMessage(EventId = 1901, EventName = "HttpsNotRequired", Level = LogLevel.Warning,
        Message = "Security:Transport:RequireHttps is false: remote clients can send credentials over plain HTTP. Use this only on an isolated test network.")]
    public static partial void HttpsNotRequired(ILogger logger);

    /// <summary>No public base URL on a server that listens beyond loopback.</summary>
    [LoggerMessage(EventId = 1902, EventName = "PublicBaseUrlMissing", Level = LogLevel.Warning,
        Message = "Security:PublicBaseUrl is not set and the server listens beyond loopback: the URLs it gives NuGet clients are built from the request's Host header. Set Security:PublicBaseUrl to the URL clients use.")]
    public static partial void PublicBaseUrlMissing(ILogger logger);

    /// <summary>An endpoint has no row in the permission table: it is denied to everyone.</summary>
    [LoggerMessage(EventId = 1903, EventName = "EndpointNotMapped", Level = LogLevel.Warning,
        Message = "The endpoint '{Endpoint}' has no entry in the permission table and is denied to everyone.")]
    public static partial void EndpointNotMapped(ILogger logger, string endpoint);

    /// <summary>The server runs in the Development environment.</summary>
    [LoggerMessage(EventId = 1904, EventName = "DevelopmentEnvironment", Level = LogLevel.Warning,
        Message = "The server runs in the Development environment: error pages show request details, including the Authorization header. Never expose a Development instance.")]
    public static partial void DevelopmentEnvironment(ILogger logger);

    /// <summary>The security database file was readable or writable by other users and was narrowed.</summary>
    [LoggerMessage(EventId = 1905, EventName = "DatabasePermissionsNarrowed", Level = LogLevel.Warning,
        Message = "The security database file '{Path}' was readable or writable by other users: its permissions were narrowed to the owner.")]
    public static partial void DatabasePermissionsNarrowed(ILogger logger, string path);

    /// <summary>The permissions of the security database could not be narrowed.</summary>
    [LoggerMessage(EventId = 1906, EventName = "DatabasePermissionsNotNarrowed", Level = LogLevel.Warning,
        Message = "The permissions of the security database could not be narrowed to the owner. Restrict access to the file and its -wal and -shm files, for example with chmod 600.")]
    public static partial void DatabasePermissionsNotNarrowed(ILogger logger);

    // ---- 2000-2099: user and API key administration by the admin commands, in UserAdministrationLog ----
}
