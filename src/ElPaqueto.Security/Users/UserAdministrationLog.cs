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
}
