using Microsoft.Extensions.Logging;

namespace ElPaqueto.Security.Users;

/// <summary>
/// Structured log events of user administration, one per state-changing operation and per refusal
/// that matters for security. Event ids are stable. Messages name the user, never a password or hash.
/// </summary>
internal static partial class UserAdministrationLog
{
    /// <summary>The logging category of these events.</summary>
    public const string Category = "ElPaqueto.Security.Administration";

    [LoggerMessage(EventId = 2001, EventName = "AdministratorBootstrapped", Level = LogLevel.Information,
        Message = "First administrator {UserName} ({UserId}) created by the bootstrap command.")]
    public static partial void AdministratorBootstrapped(ILogger logger, string userName, Guid userId);

    [LoggerMessage(EventId = 2002, EventName = "BootstrapRefused", Level = LogLevel.Information,
        Message = "Bootstrap refused for {UserName}: {Reason}.")]
    public static partial void BootstrapRefused(ILogger logger, string userName, string reason);

    [LoggerMessage(EventId = 2003, EventName = "UserCreated", Level = LogLevel.Information,
        Message = "User {UserName} ({UserId}) created with permissions {Permissions}.")]
    public static partial void UserCreated(ILogger logger, string userName, Guid userId, Permission permissions);

    [LoggerMessage(EventId = 2004, EventName = "UserDisabled", Level = LogLevel.Information,
        Message = "User {UserName} ({UserId}) disabled.")]
    public static partial void UserDisabled(ILogger logger, string userName, Guid userId);

    [LoggerMessage(EventId = 2005, EventName = "UserEnabled", Level = LogLevel.Information,
        Message = "User {UserName} ({UserId}) enabled.")]
    public static partial void UserEnabled(ILogger logger, string userName, Guid userId);

    [LoggerMessage(EventId = 2006, EventName = "UserPermissionsChanged", Level = LogLevel.Information,
        Message = "Permissions of user {UserName} ({UserId}) changed from {OldPermissions} to {NewPermissions}.")]
    public static partial void UserPermissionsChanged(ILogger logger, string userName, Guid userId, Permission oldPermissions, Permission newPermissions);

    [LoggerMessage(EventId = 2007, EventName = "UserPasswordReset", Level = LogLevel.Information,
        Message = "Password of user {UserName} ({UserId}) reset.")]
    public static partial void UserPasswordReset(ILogger logger, string userName, Guid userId);

    [LoggerMessage(EventId = 2008, EventName = "LastActiveAdministratorProtected", Level = LogLevel.Information,
        Message = "Change to user {UserName} refused: it would leave no active administrator.")]
    public static partial void LastActiveAdministratorProtected(ILogger logger, string userName);

    [LoggerMessage(EventId = 2009, EventName = "ConcurrentChangeRejected", Level = LogLevel.Warning,
        Message = "Change to user {UserName} rejected: the user was changed concurrently.")]
    public static partial void ConcurrentChangeRejected(ILogger logger, string userName);

    [LoggerMessage(EventId = 2010, EventName = "ApiKeyCreated", Level = LogLevel.Information,
        Message = "API key {KeyId} created by the command line for user {UserName} ({UserId}) with permissions {Permissions}, expiring at {ExpiresAt} (UTC; null: never).")]
    public static partial void ApiKeyCreated(ILogger logger, string keyId, string userName, Guid userId, Permission permissions, DateTime? expiresAt);

    [LoggerMessage(EventId = 2011, EventName = "ApiKeyRevoked", Level = LogLevel.Information,
        Message = "API key {KeyId} of user {UserName} ({UserId}) revoked by the command line.")]
    public static partial void ApiKeyRevoked(ILogger logger, string keyId, string userName, Guid userId);

    [LoggerMessage(EventId = 2012, EventName = "ApiKeyRotated", Level = LogLevel.Information,
        Message = "API key {OldKeyId} of user {UserName} ({UserId}) rotated by the command line: replaced by {NewKeyId}, expiring at {ExpiresAt} (UTC; null: never).")]
    public static partial void ApiKeyRotated(ILogger logger, string oldKeyId, string newKeyId, string userName, Guid userId, DateTime? expiresAt);
}
