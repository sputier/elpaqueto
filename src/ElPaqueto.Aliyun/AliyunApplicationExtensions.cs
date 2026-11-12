using System;
using Aliyun.OSS;
using ElPaqueto.Aliyun;
using ElPaqueto.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ElPaqueto;

public static class AliyunApplicationExtensions
{
    public static ElPaquetoApplication AddAliyunOssStorage(this ElPaquetoApplication app)
    {
        app.Services.AddElPaquetoOptions<AliyunStorageOptions>(nameof(ElPaquetoOptions.Storage));

        app.Services.AddTransient<AliyunStorageService>();
        app.Services.TryAddTransient<IStorageService>(provider => provider.GetRequiredService<AliyunStorageService>());

        app.Services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AliyunStorageOptions>>().Value;

            return new OssClient(options.Endpoint, options.AccessKey, options.AccessKeySecret);
        });

        app.Services.AddProvider<IStorageService>((provider, config) =>
        {
            if (!config.HasStorageType("AliyunOss")) return null;

            return provider.GetRequiredService<AliyunStorageService>();
        });

        return app;
    }

    public static ElPaquetoApplication AddAliyunOssStorage(
        this ElPaquetoApplication app,
        Action<AliyunStorageOptions> configure)
    {
        app.AddAliyunOssStorage();
        app.Services.Configure(configure);
        return app;
    }
}
