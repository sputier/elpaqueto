using ElPaqueto.Security.Cli;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.Hosting;

namespace ElPaqueto.Security;

/// <summary>
/// Entry point of the security module's command-line commands.
/// </summary>
public static class ElPaquetoCommandLineExtensions
{
    /// <summary>
    /// Adds the <c>admin</c> command group: the administration of users from the command line.
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

        builder.Group(app, "admin", "Administer users from the command line.", admin =>
        {
            BootstrapCommand.Register(builder, admin);
            UserCommands.Register(builder, admin);
        });

        return app;
    }
}
