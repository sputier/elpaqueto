using System;
using System.IO;
using System.Threading.Tasks;
using ElPaqueto.Core;
using ElPaqueto.Security;
using ElPaqueto.Web;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElPaqueto;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Everything runs inside the try: building the host reads every configuration source
        // (appsettings.json, /run/secrets...), and a file that cannot be read or parsed must stop the
        // process with a message and an exit code, not with an unhandled exception.
        try
        {
            await RunAsync(args);
        }
        catch (ConfigurationLoadException e)
        {
            // Only the message: it names the file, never a value. The inner exception of a parse error
            // can quote the content of the file, which may be a secret.
            Console.Error.WriteLine("ElPaqueto could not load its configuration: " + e.Message);
            Environment.ExitCode = 1;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Exit with a code instead of crashing: an unhandled exception aborts the process, which on
            // Linux can write a core dump containing process memory, connection strings included.
            // The host may be disposed or not built here, so write to the console like the runtime would.
            // Exception messages are not printed: some repeat configuration values (the configuration
            // binder quotes a value it cannot convert), which may be secrets. Types and stack traces locate
            // the failure without them.
            Console.Error.WriteLine("ElPaqueto stopped because of an unhandled error:");
            WriteWithoutMessages(e);
            Environment.ExitCode = 1;
        }
    }

    private static void WriteWithoutMessages(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current != exception)
            {
                Console.Error.WriteLine("Caused by:");
            }

            Console.Error.WriteLine(current.GetType().FullName);
            Console.Error.WriteLine(current.StackTrace);
        }
    }

    private static async Task RunAsync(string[] args)
    {
        IHost host;
        try
        {
            host = CreateHostBuilder(args).Build();
        }
        catch (Exception e) when (IsConfigurationLoadFailure(e))
        {
            throw new ConfigurationLoadException(e);
        }

        // Disposed on every path, so that the console logger flushes what it queued before the process exits.
        try
        {
            await RunAsync(host, args);
        }
        finally
        {
            if (host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                host.Dispose();
            }
        }
    }

    private static async Task RunAsync(IHost host, string[] args)
    {
        var upgradeLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ElPaqueto.Upgrade");
        BaGetterUpgradeChecks.ResolveConfigRoot(Environment.GetEnvironmentVariable, out var fromLegacyVariable);
        if (fromLegacyVariable)
        {
            upgradeLogger.LogWarning(
                "The environment variable {LegacyVariable} is deprecated: rename it to {Variable}. See {Guide}.",
                BaGetterUpgradeChecks.LegacyConfigRootVariable,
                BaGetterUpgradeChecks.ConfigRootVariable,
                BaGetterUpgradeChecks.UpgradeGuide);
        }

        var configurationIsValid = host.ValidateStartupOptions();
        configurationIsValid &= host.ValidateElPaquetoSecurityOptions();
        if (!configurationIsValid)
        {
            Environment.ExitCode = 1;
            return;
        }

        var app = new CommandLineApplication
        {
            Name = "elpaqueto",
            Description = "A light-weight NuGet service",
        };

        app.HelpOption(inherited: true);

        app.Command("import", import =>
        {
            import.Command("downloads", downloads =>
            {
                downloads.OnExecuteAsync(async cancellationToken =>
                {
                    using var scope = host.Services.CreateScope();
                    var importer = scope.ServiceProvider.GetRequiredService<DownloadsImporter>();

                    await importer.ImportAsync(cancellationToken);
                });
            });
        });

        app.AddElPaquetoCommands(host);

        app.Option("--urls", "The URLs that ElPaqueto should bind to.", CommandOptionType.SingleValue);

        app.OnExecuteAsync(async cancellationToken =>
        {
            // Before the migrations, which create the configured database file.
            WarnAboutLegacyDatabase(host, upgradeLogger);

            await host.RunMigrationsAsync(cancellationToken);
            await host.RunAsync(cancellationToken);
        });

        await app.ExecuteAsync(args);
    }

    /// <summary>
    /// Whether an exception thrown while building the host means that a configuration source could not be
    /// read: an unreadable or missing file or folder (the key-per-file source of <c>/run/secrets</c> throws these
    /// directly), or a file that a file-based source failed to load or parse (<see cref="InvalidDataException"/>,
    /// which names the file).
    /// </summary>
    public static bool IsConfigurationLoadFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or InvalidDataException;

    private static void WarnAboutLegacyDatabase(IHost host, ILogger logger)
    {
        var database = host.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (BaGetterUpgradeChecks.TryFindLegacyDatabase(database.Type, database.ConnectionString, out var configuredPath, out var legacyPath))
        {
            logger.LogWarning(
                "The database file {ConfiguredPath} does not exist, but a BaGetter database {LegacyPath} is in the same folder. "
                + "ElPaqueto now creates a new, empty database. To keep your packages, stop ElPaqueto, delete the new file, "
                + "then rename the BaGetter file or point Database:ConnectionString to it. See {Guide}.",
                configuredPath,
                legacyPath,
                BaGetterUpgradeChecks.UpgradeGuide);
        }
    }

    public static IHostBuilder CreateHostBuilder(string[] args)
    {
        return Host
            .CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((ctx, config) =>
            {
                // BAGET_CONFIG_ROOT is a deprecated fallback, reported once the host is built.
                var root = BaGetterUpgradeChecks.ResolveConfigRoot(Environment.GetEnvironmentVariable, out _);

                if (!string.IsNullOrEmpty(root))
                {
                    config.SetBasePath(root);
                }

                // Optionally load secrets from files in the conventional path
                config.AddKeyPerFile("/run/secrets", optional: true);
            })
            .ConfigureWebHostDefaults(web =>
            {
                web.ConfigureKestrel(options =>
                {
                    // Remove the upload limit from Kestrel. If needed, an upload limit can
                    // be enforced by a reverse proxy server, like IIS.
                    options.Limits.MaxRequestBodySize = null;
                });

                web.UseStartup<Startup>();
            });
    }

    /// <summary>A configuration source could not be read or parsed while the host was built.</summary>
    private sealed class ConfigurationLoadException(Exception inner) : Exception(inner.Message, inner);
}
