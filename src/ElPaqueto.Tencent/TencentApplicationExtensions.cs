using ElPaqueto.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Tencent;

public static class TencentApplicationExtensions
{
    public static ElPaquetoApplication AddTencentOssStorage(this ElPaquetoApplication app)
    {
        app.Services.AddElPaquetoOptions<TencentStorageOptions>(nameof(ElPaquetoOptions.Storage));

        app.Services.AddTransient<TencentStorageService>();
        app.Services.TryAddTransient<IStorageService>(provider => provider.GetRequiredService<TencentStorageService>());

        app.Services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<TencentStorageOptions>>().Value;

            return new TencentCosClient(options);
        });

        app.Services.AddProvider<IStorageService>((provider, config) =>
        {
            if (!config.HasStorageType("TencentCos"))
                return null;

            return provider.GetRequiredService<TencentStorageService>();
        });

        return app;
    }

    public static ElPaquetoApplication AddTencentOssStorage(
        this ElPaquetoApplication app,
        Action<TencentStorageOptions> configure)
    {
        app.AddTencentOssStorage();
        app.Services.Configure(configure);
        return app;
    }
}
