using ElPaqueto.Security.Data;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ElPaqueto.Security.Cli;

/// <summary>
/// What a running <c>admin</c> command receives: its parsed arguments, the console, and services
/// from a scope created for this execution.
/// </summary>
internal sealed class AdminInvocation(ParsedArguments arguments, IConsole console, IServiceProvider services, string commandPath)
{
    public ParsedArguments Arguments { get; } = arguments;

    public IConsole Console { get; } = console;

    public IServiceProvider Services { get; } = services;

    /// <summary>The full command, for messages: <c>elpaqueto admin bootstrap</c>.</summary>
    public string CommandPath { get; } = commandPath;

    /// <summary>Writes a diagnostic to the error stream and returns <paramref name="exitCode"/>.</summary>
    public int Fail(int exitCode, string message)
    {
        Console.Error.WriteLine(message);
        return exitCode;
    }

    /// <summary>Reports a usage error and how to get help.</summary>
    public int UsageError(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine($"Run '{CommandPath} --help' for usage.");
        return AdminExitCode.UsageError;
    }

    /// <summary>Applies the pending migrations of the security database (never of the package database).</summary>
    public Task MigrateAsync(CancellationToken cancellationToken)
        => Services.GetRequiredService<IAuthDatabaseMigrator>().MigrateAsync(cancellationToken);
}

/// <summary>
/// Builds <c>admin</c> command groups and commands with the shared conventions: strict argument
/// parsing, usage errors with exit code 2, unexpected errors with exit code 1, and the exit code
/// reported to the host.
/// </summary>
internal sealed partial class AdminCommandBuilder(IServiceProvider services, Action<int> reportExitCode)
{
    /// <summary>Adds a command group: it only dispatches to its subcommands.</summary>
    public CommandLineApplication Group(CommandLineApplication parent, string name, string description, Action<CommandLineApplication> configure)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(configure);

        return parent.Command(name, group =>
        {
            group.Description = description;

            // Collected instead of thrown: the library would repeat the argument in its error.
            group.UnrecognizedArgumentHandling = UnrecognizedArgumentHandling.CollectAndContinue;

            configure(group);

            group.OnExecute(() =>
            {
                var console = Console(group);
                if (group.RemainingArguments.Count > 0)
                {
                    // The unknown word is not repeated: it may be a misplaced secret.
                    console.Error.WriteLine("Unknown command.");
                }

                group.ShowHelp();
                return Report(AdminExitCode.UsageError);
            });
        });
    }

    /// <summary>Adds a command that runs <paramref name="handler"/> with its parsed arguments.</summary>
    public CommandLineApplication Command(
        CommandLineApplication parent,
        string name,
        string description,
        IReadOnlyList<AdminArgument> arguments,
        IReadOnlyList<AdminOption> options,
        string? remarks,
        Func<AdminInvocation, CancellationToken, Task<int>> handler)
    {
        ArgumentNullException.ThrowIfNull(parent);

        return parent.Command(name, command =>
        {
            command.Description = description;
            command.UnrecognizedArgumentHandling = UnrecognizedArgumentHandling.CollectAndContinue;
            command.ExtendedHelpText = CommandLineSyntax.Describe(arguments, options, remarks);

            command.OnExecuteAsync(async cancellationToken =>
            {
                var console = Console(command);
                var path = FullName(command);

                if (!CommandLineSyntax.TryParse(command.RemainingArguments, options, arguments, out var parsed, out var error))
                {
                    console.Error.WriteLine(error);
                    console.Error.WriteLine($"Run '{path} --help' for usage.");
                    return Report(AdminExitCode.UsageError);
                }

                using var scope = services.CreateScope();
                var invocation = new AdminInvocation(parsed, console, scope.ServiceProvider, path);

                try
                {
                    return Report(await handler(invocation, cancellationToken));
                }
                catch (Exception e)
                {
                    // The message is shown, not the stack trace. Nothing written here can hold a password:
                    // passwords never enter exception messages.
                    LogCommandFailed(scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(Users.UserAdministrationLog.Category), path, e);
                    return Report(invocation.Fail(AdminExitCode.UnexpectedError, $"The command failed: {e.Message}"));
                }
            });
        });
    }

    private static IConsole Console(CommandLineApplication command)
        => (IConsole?)((IServiceProvider)command).GetService(typeof(IConsole)) ?? PhysicalConsole.Singleton;

    private static string FullName(CommandLineApplication command)
    {
        var names = new List<string>();
        for (var current = command; current is not null; current = current.Parent)
        {
            if (!string.IsNullOrEmpty(current.Name))
            {
                names.Add(current.Name);
            }
        }

        names.Reverse();
        return string.Join(' ', names);
    }

    private int Report(int exitCode)
    {
        reportExitCode(exitCode);
        return exitCode;
    }

    [LoggerMessage(EventId = 2099, EventName = "AdminCommandFailed", Level = LogLevel.Error, Message = "The command '{Command}' failed.")]
    private static partial void LogCommandFailed(ILogger logger, string command, Exception exception);
}
