using System;
using System.Threading.Tasks;
using ElPaqueto.Core;
using ElPaqueto.Security;
using ElPaqueto.Web;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ElPaqueto;

public class Program
{
    public static async Task Main(string[] args)
    {
        var host = CreateHostBuilder(args).Build();
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

        app.Option("--urls", "The URLs that ElPaqueto should bind to.", CommandOptionType.SingleValue);

        app.OnExecuteAsync(async cancellationToken =>
        {
            await host.RunMigrationsAsync(cancellationToken);
            await host.RunAsync(cancellationToken);
        });

        try
        {
            await app.ExecuteAsync(args);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Exit with a code instead of crashing: an unhandled exception aborts the process, which on
            // Linux can write a core dump containing process memory, connection strings included.
            // The host is already disposed here, so write to the console like the runtime would.
            Console.Error.WriteLine("ElPaqueto stopped because of an unhandled error:");
            Console.Error.WriteLine(e);
            Environment.ExitCode = 1;
        }
    }

    public static IHostBuilder CreateHostBuilder(string[] args)
    {
        return Host
            .CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((ctx, config) =>
            {
                var root = Environment.GetEnvironmentVariable("ELPAQUETO_CONFIG_ROOT");

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
}
