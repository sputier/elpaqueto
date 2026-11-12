using ElPaqueto.Security.Passwords;
using ElPaqueto.Security.Users;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;

namespace ElPaqueto.Security.Cli;

/// <summary>
/// <c>admin bootstrap --username &lt;name&gt; [password input]</c>: creates the first administrator.
/// </summary>
internal static class BootstrapCommand
{
    private const string UserNameOption = "--username";

    public static void Register(AdminCommandBuilder builder, CommandLineApplication admin)
    {
        builder.Command(
            admin,
            "bootstrap",
            "Create the first administrator. Refused (exit code 3) once any user holding the admin permission exists.",
            [],
            [new(UserNameOption, "name", "The user name: 1 to 64 characters from A-Z a-z 0-9 . _ -"), .. PasswordInput.Options],
            PasswordInput.Remarks,
            RunAsync);
    }

    private static async Task<int> RunAsync(AdminInvocation invocation, CancellationToken cancellationToken)
    {
        var userName = invocation.Arguments.Value(UserNameOption);
        if (userName is null)
        {
            return invocation.UsageError($"The option '{UserNameOption}' is required.");
        }

        // Everything that can be checked without the database is checked first: an invalid
        // invocation must not even create the database file.
        var nameError = UserNames.Validate(userName);
        if (nameError is not null)
        {
            return invocation.UsageError(nameError);
        }

        var passwordInput = new PasswordInput(invocation.Console, invocation.Services.GetRequiredService<ISecretPrompt>());
        if (!passwordInput.TryRead(invocation.Arguments, out var password, out var passwordError))
        {
            return invocation.UsageError(passwordError);
        }

        var policyErrors = invocation.Services.GetRequiredService<IPasswordService>().Validate(password, userName);
        if (policyErrors.Count > 0)
        {
            return invocation.UsageError(string.Join(Environment.NewLine, policyErrors));
        }

        await invocation.MigrateAsync(cancellationToken);

        var result = await invocation.Services.GetRequiredService<UserService>()
            .BootstrapAsync(userName, password, cancellationToken);

        return result.Status switch
        {
            UserOperationStatus.Succeeded => Created(invocation, result.User!),
            UserOperationStatus.AdministratorExists => invocation.Fail(
                AdminExitCode.PreconditionFailed,
                "An administrator already exists: nothing was changed. The bootstrap command only creates the first administrator: "
                + "create further users with 'admin user create', recover access with 'admin user enable' or 'admin user reset-password'."),
            UserOperationStatus.UserNameTaken => invocation.Fail(
                AdminExitCode.PreconditionFailed,
                $"A user named '{userName}' already exists (user names ignore case): nothing was changed."),
            UserOperationStatus.InvalidInput => invocation.UsageError(string.Join(Environment.NewLine, result.Errors)),
            _ => invocation.Fail(AdminExitCode.UnexpectedError, "The administrator could not be created: retry."),
        };
    }

    private static int Created(AdminInvocation invocation, UserSummary user)
    {
        invocation.Console.Out.WriteLine(
            $"Created administrator '{user.UserName}' ({user.Id}) with permissions: {PermissionText.Format(user.Permissions)}.");
        return AdminExitCode.Success;
    }
}
