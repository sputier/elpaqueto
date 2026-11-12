using System;
using ElPaqueto.Core;
using ElPaqueto.Core.Statistics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElPaqueto;

public static class ElPaquetoApplicationExtensions
{
    public static ElPaquetoApplication AddFileStorage(this ElPaquetoApplication app)
    {
        app.Services.TryAddTransient<IStorageService>(provider => provider.GetRequiredService<FileStorageService>());

        return app;
    }

    public static ElPaquetoApplication AddFileStorage(
        this ElPaquetoApplication app,
        Action<FileSystemStorageOptions> configure)
    {
        app.AddFileStorage();
        app.Services.Configure(configure);
        return app;
    }

    public static ElPaquetoApplication AddNullStorage(this ElPaquetoApplication app)
    {
        app.Services.TryAddTransient<IStorageService>(provider => provider.GetRequiredService<NullStorageService>());
        return app;
    }

    public static ElPaquetoApplication AddNullSearch(this ElPaquetoApplication app)
    {
        app.Services.TryAddTransient<ISearchIndexer>(provider => provider.GetRequiredService<NullSearchIndexer>());
        app.Services.TryAddTransient<ISearchService>(provider => provider.GetRequiredService<NullSearchService>());
        return app;
    }

    public static ElPaquetoApplication AddStatistics(this ElPaquetoApplication app)
    {
        app.Services.TryAddSingleton<IStatisticsService, StatisticsService>();
        return app;
    }
}
