using System;
using System.Threading;
using System.Threading.Tasks;
using ElPaqueto.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Web;

public static class IHostExtensions
{
    public static IHostBuilder UseElPaqueto(this IHostBuilder host, Action<ElPaquetoApplication> configure)
    {
        return host.ConfigureServices(services =>
        {
            services.AddElPaquetoWebApplication(configure);
        });
    }

    public static async Task RunMigrationsAsync(
        this IHost host,
        CancellationToken cancellationToken = default)
    {
        // Run migrations if necessary.
        var options = host.Services.GetRequiredService<IOptions<ElPaquetoOptions>>();

        if (options.Value.RunMigrationsAtStartup)
        {
            using var scope = host.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetService<IContext>();
            if (ctx != null)
            {
                await ctx.RunMigrationsAsync(cancellationToken);
            }
        }
    }

    public static bool ValidateStartupOptions(this IHost host)
    {
        return host
            .Services
            .GetRequiredService<ValidateStartupOptions>()
            .Validate();
    }
}
