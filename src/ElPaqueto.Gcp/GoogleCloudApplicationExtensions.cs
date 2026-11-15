using System;
using ElPaqueto.Core;
using ElPaqueto.Gcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElPaqueto;

public static class GoogleCloudApplicationExtensions
{
    public static ElPaquetoApplication AddGoogleCloudStorage(this ElPaquetoApplication app)
    {
        app.Services.AddElPaquetoOptions<GoogleCloudStorageOptions>(nameof(ElPaquetoOptions.Storage));
        app.Services.AddTransient<GoogleCloudStorageService>();

        app.Services.TryAddTransient<IStorageService>(provider => provider.GetRequiredService<GoogleCloudStorageService>());

        app.Services.AddProvider<IStorageService>((provider, config) =>
        {
            if (!config.HasStorageType("GoogleCloud")) return null;

            return provider.GetRequiredService<GoogleCloudStorageService>();
        });

        return app;
    }

    public static ElPaquetoApplication AddGoogleCloudStorage(
        this ElPaquetoApplication app,
        Action<GoogleCloudStorageOptions> configure)
    {
        app.AddGoogleCloudStorage();
        app.Services.Configure(configure);
        return app;
    }
}
