using ElPaqueto.Security.Cli;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace ElPaqueto.Security;

/// <summary>
/// Entry point of the security module's command-line commands.
/// </summary>
public static class ElPaquetoCommandLineExtensions
{
    /// <summary>
    /// Adds the <c>admin</c> command group: the administration of users and API keys from the command line.
    /// </summary>
    /// <param name="app">The root command-line application.</param>
    /// <param name="host">The built host. Its hosted services are never started by these commands.</param>
    /// <returns>The same <paramref name="app"/> instance, to allow chaining.</returns>
    /// <remarks>
    /// The commands migrate the security database themselves and never migrate the package database,
    /// start the web server or bind a port. Their exit code is set on <see cref="Environment.ExitCode"/>.
    /// There is no authentication: whoever can run the server binary against its configuration and
    /// security database administers it.
    /// </remarks>
    public static CommandLineApplication AddElPaquetoCommands(this CommandLineApplication app, IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        return AddElPaquetoCommands(app, host.Services, exitCode => Environment.ExitCode = exitCode);
    }

    /// <summary>
    /// When <paramref name="args"/> run an <c>admin</c> command, sends every console log line to standard
    /// error, so that standard output only carries the command's result: a key created by
    /// <c>admin key create</c> can be redirected to a file without log lines around it.
    /// </summary>
    /// <param name="builder">The host builder, before it is built.</param>
    /// <param name="args">The process arguments.</param>
    /// <returns>The same <paramref name="builder"/> instance, to allow chaining.</returns>
    public static IHostBuilder ConfigureElPaquetoCommandLogging(this IHostBuilder builder, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        if (IsAdminCommand(args))
        {
            builder.ConfigureServices(services => services.Configure<ConsoleLoggerOptions>(
                options => options.LogToStandardErrorThreshold = LogLevel.Trace));
        }

        return builder;
    }

    /// <summary>
    /// Whether the process arguments may run an <c>admin</c> command: any argument is <c>admin</c>, wherever
    /// root options put it. A false positive only moves log lines to standard error.
    /// </summary>
    internal static bool IsAdminCommand(IReadOnlyList<string> args)
        => args.Any(a => string.Equals(a, "admin", StringComparison.Ordinal));

    /// <summary>Adds the commands, reporting exit codes to <paramref name="reportExitCode"/>.</summary>
    internal static CommandLineApplication AddElPaquetoCommands(
        CommandLineApplication app,
        IServiceProvider services,
        Action<int> reportExitCode)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(reportExitCode);

        var builder = new AdminCommandBuilder(services, reportExitCode);

        builder.Group(app, "admin", "Administer users and API keys from the command line.", admin =>
        {
            BootstrapCommand.Register(builder, admin);
            UserCommands.Register(builder, admin);
            ApiKeyCommands.Register(builder, admin);
        });

        return app;
    }
}
