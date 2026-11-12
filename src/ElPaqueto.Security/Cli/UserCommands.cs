using ElPaqueto.Security.Authentication;
using ElPaqueto.Security.Passwords;
using ElPaqueto.Security.Users;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;

namespace ElPaqueto.Security.Cli;

/// <summary>
/// <c>admin user …</c>: create, list, show, disable, enable, set-permissions, reset-password.
/// </summary>
internal static class UserCommands
{
    private const string PermissionsOption = "--permissions";
    private const string IncludeDisabledOption = "--include-disabled";

    private static readonly AdminArgument UserNameArgument = new("username", "The user name (case-insensitive).");

    private static readonly AdminOption PermissionsOptionSpec = new(
        PermissionsOption,
        "list",
        "Comma-separated: read, publish, admin, or a preset: reader, publisher, administrator.");

    public static void Register(AdminCommandBuilder builder, CommandLineApplication admin)
    {
        builder.Group(admin, "user", "Create users and change their access.", user =>
        {
            builder.Command(
                user,
                "create",
                "Create an active user with the given permissions.",
                [UserNameArgument],
                [PermissionsOptionSpec, .. PasswordInput.Options],
                PasswordInput.Remarks,
                CreateAsync);

            builder.Command(
                user,
                "list",
                "List the active users, or every user with --include-disabled.",
                [],
                [UserOutput.FormatOptionSpec, new(IncludeDisabledOption, null, "Include disabled users.")],
                null,
                ListAsync);

            builder.Command(
                user,
                "show",
                "Show one user.",
                [UserNameArgument],
                [UserOutput.FormatOptionSpec],
                null,
                ShowAsync);

            builder.Command(
                user,
                "disable",
                "Disable a user. Refused for the last active administrator.",
                [UserNameArgument],
                [],
                null,
                (invocation, cancellationToken) => SetActiveAsync(invocation, active: false, cancellationToken));

            builder.Command(
                user,
                "enable",
                "Enable a disabled user.",
                [UserNameArgument],
                [],
                null,
                (invocation, cancellationToken) => SetActiveAsync(invocation, active: true, cancellationToken));

            builder.Command(
                user,
                "set-permissions",
                "Replace the permissions of a user. Refused if it would leave no active administrator.",
                [UserNameArgument],
                [PermissionsOptionSpec],
                null,
                SetPermissionsAsync);

            builder.Command(
                user,
                "reset-password",
                "Set a new password for a user.",
                [UserNameArgument],
                [.. PasswordInput.Options],
                PasswordInput.Remarks,
                ResetPasswordAsync);
        });
    }

    private static async Task<int> CreateAsync(AdminInvocation invocation, CancellationToken cancellationToken)
    {
        var userName = invocation.Arguments.Positionals[0];
        if (!TryValidateUserName(invocation, userName, out var failure)
            || !TryReadPermissions(invocation, out var permissions, out failure)
            || !TryReadPassword(invocation, userName, out var password, out failure))
        {
            return failure;
        }

        await invocation.MigrateAsync(cancellationToken);
        var result = await Users(invocation).CreateAsync(userName, password, permissions, cancellationToken);

        if (result.Status == UserOperationStatus.Succeeded)
        {
            invocation.Console.Out.WriteLine(
                $"Created user '{result.User!.UserName}' ({result.User.Id}) with permissions: {PermissionText.Format(result.User.Permissions)}.");
            return AdminExitCode.Success;
        }

        return Report(invocation, userName, result);
    }

    private static async Task<int> ListAsync(AdminInvocation invocation, CancellationToken cancellationToken)
    {
        if (!UserOutput.TryReadFormat(invocation.Arguments, out var json, out var error))
        {
            return invocation.UsageError(error);
        }

        await invocation.MigrateAsync(cancellationToken);
        var users = await Users(invocation).ListAsync(invocation.Arguments.Has(IncludeDisabledOption), cancellationToken);

        invocation.Console.Out.Write(json ? UserOutput.ListAsJson(users) : UserOutput.ListAsText(users));
        return AdminExitCode.Success;
    }

    private static async Task<int> ShowAsync(AdminInvocation invocation, CancellationToken cancellationToken)
    {
        var userName = invocation.Arguments.Positionals[0];
        if (!TryValidateUserName(invocation, userName, out var failure))
        {
            return failure;
        }

        if (!UserOutput.TryReadFormat(invocation.Arguments, out var json, out var error))
        {
            return invocation.UsageError(error);
        }

        await invocation.MigrateAsync(cancellationToken);
        var user = await Users(invocation).FindAsync(userName, cancellationToken);
        if (user is null)
        {
            return NotFound(invocation, userName);
        }

        invocation.Console.Out.Write(json ? UserOutput.UserAsJson(user) : UserOutput.UserAsText(user));
        return AdminExitCode.Success;
    }

    private static async Task<int> SetActiveAsync(AdminInvocation invocation, bool active, CancellationToken cancellationToken)
    {
        var userName = invocation.Arguments.Positionals[0];
        if (!TryValidateUserName(invocation, userName, out var failure))
        {
            return failure;
        }

        await invocation.MigrateAsync(cancellationToken);
        var result = await Users(invocation).SetActiveAsync(userName, active, cancellationToken);

        switch (result.Status)
        {
            case UserOperationStatus.Succeeded:
                invocation.Console.Out.WriteLine(active
                    ? $"Enabled user '{result.User!.UserName}'."
                    : $"Disabled user '{result.User!.UserName}'.");
                return AdminExitCode.Success;
            case UserOperationStatus.Unchanged:
                invocation.Console.Error.WriteLine(active
                    ? $"User '{result.User!.UserName}' is already enabled: nothing was changed."
                    : $"User '{result.User!.UserName}' is already disabled: nothing was changed.");
                return AdminExitCode.Success;
            default:
                return Report(invocation, userName, result);
        }
    }

    private static async Task<int> SetPermissionsAsync(AdminInvocation invocation, CancellationToken cancellationToken)
    {
        var userName = invocation.Arguments.Positionals[0];
        if (!TryValidateUserName(invocation, userName, out var failure)
            || !TryReadPermissions(invocation, out var permissions, out failure))
        {
            return failure;
        }

        await invocation.MigrateAsync(cancellationToken);
        var result = await Users(invocation).SetPermissionsAsync(userName, permissions, cancellationToken);

        switch (result.Status)
        {
            case UserOperationStatus.Succeeded:
                invocation.Console.Out.WriteLine(
                    $"Permissions of '{result.User!.UserName}' set to: {PermissionText.Format(result.User.Permissions)}.");
                return AdminExitCode.Success;
            case UserOperationStatus.Unchanged:
                invocation.Console.Error.WriteLine(
                    $"User '{result.User!.UserName}' already has exactly these permissions: nothing was changed.");
                return AdminExitCode.Success;
            default:
                return Report(invocation, userName, result);
        }
    }

    private static async Task<int> ResetPasswordAsync(AdminInvocation invocation, CancellationToken cancellationToken)
    {
        var userName = invocation.Arguments.Positionals[0];
        if (!TryValidateUserName(invocation, userName, out var failure))
        {
            return failure;
        }

        // The user is looked up first: nobody is asked for the password of a user that does not exist.
        await invocation.MigrateAsync(cancellationToken);
        if (await Users(invocation).FindAsync(userName, cancellationToken) is null)
        {
            return NotFound(invocation, userName);
        }

        if (!TryReadPassword(invocation, userName, out var password, out failure))
        {
            return failure;
        }

        var result = await Users(invocation).ResetPasswordAsync(userName, password, cancellationToken);

        if (result.Status != UserOperationStatus.Succeeded)
        {
            return Report(invocation, userName, result);
        }

        invocation.Console.Out.WriteLine($"Password of '{result.User!.UserName}' changed.");
        if (!result.User.IsActive)
        {
            invocation.Console.Error.WriteLine($"User '{result.User.UserName}' is disabled: enable it to let it sign in.");
        }

        invocation.Console.Error.WriteLine(
            "Other credentials of the user are not affected. If the account may be compromised, also review its other credentials.");
        return AdminExitCode.Success;
    }

    private static UserService Users(AdminInvocation invocation) => invocation.Services.GetRequiredService<UserService>();

    private static bool TryValidateUserName(AdminInvocation invocation, string userName, out int failure)
    {
        var error = UserNames.Validate(userName);
        failure = error is null ? AdminExitCode.Success : invocation.UsageError(error);
        return error is null;
    }

    private static bool TryReadPermissions(AdminInvocation invocation, out Permission permissions, out int failure)
    {
        permissions = Permission.None;
        var value = invocation.Arguments.Value(PermissionsOption);
        if (value is null)
        {
            failure = invocation.UsageError($"The option '{PermissionsOption}' is required. Valid values: {PermissionText.ValidValues}.");
            return false;
        }

        if (!PermissionText.TryParse(value, out permissions, out var error))
        {
            failure = invocation.UsageError(error);
            return false;
        }

        failure = AdminExitCode.Success;
        return true;
    }

    private static bool TryReadPassword(AdminInvocation invocation, string userName, out string password, out int failure)
    {
        var input = new PasswordInput(invocation.Console, invocation.Services.GetRequiredService<ISecretPrompt>());
        if (!input.TryRead(invocation.Arguments, out password, out var error))
        {
            failure = invocation.UsageError(error);
            return false;
        }

        var policyErrors = invocation.Services.GetRequiredService<IPasswordService>().Validate(password, userName);
        if (policyErrors.Count > 0)
        {
            failure = invocation.UsageError(string.Join(Environment.NewLine, policyErrors));
            return false;
        }

        failure = AdminExitCode.Success;
        return true;
    }

    private static int NotFound(AdminInvocation invocation, string userName)
        => invocation.Fail(AdminExitCode.NotFound, $"User '{userName}' does not exist.");

    private static int Report(AdminInvocation invocation, string userName, UserOperationResult result) => result.Status switch
    {
        UserOperationStatus.NotFound => NotFound(invocation, userName),
        UserOperationStatus.UserNameTaken => invocation.Fail(
            AdminExitCode.PreconditionFailed,
            $"A user named '{userName}' already exists (user names ignore case): nothing was changed."),
        UserOperationStatus.LastActiveAdministrator => invocation.Fail(
            AdminExitCode.PreconditionFailed,
            $"Refused: '{userName}' is the last active user holding the admin permission. Give another active user the admin permission first. Nothing was changed."),
        UserOperationStatus.InvalidInput => invocation.UsageError(string.Join(Environment.NewLine, result.Errors)),
        UserOperationStatus.ConcurrencyConflict => invocation.Fail(
            AdminExitCode.UnexpectedError,
            $"User '{userName}' was changed by someone else at the same time: nothing was changed. Retry."),
        _ => invocation.Fail(AdminExitCode.UnexpectedError, "The command failed: retry."),
    };
}
