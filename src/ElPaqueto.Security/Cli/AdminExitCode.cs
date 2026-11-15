namespace ElPaqueto.Security.Cli;

/// <summary>
/// Exit codes of every <c>admin</c> command. The values are part of the command-line contract:
/// scripts depend on them, so they never change.
/// </summary>
internal static class AdminExitCode
{
    /// <summary>The command did what was asked, or the requested state was already in place.</summary>
    public const int Success = 0;

    /// <summary>An unexpected error, for example a database that cannot be opened. Retrying may help.</summary>
    public const int UnexpectedError = 1;

    /// <summary>Invalid arguments or input: unknown option, invalid user name, password refused by the policy.</summary>
    public const int UsageError = 2;

    /// <summary>A precondition failed, for example an administrator already exists. Nothing was changed.</summary>
    public const int PreconditionFailed = 3;

    /// <summary>The user named by the command does not exist.</summary>
    public const int NotFound = 4;
}
