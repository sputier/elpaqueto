using System;
using System.Text.Json.Serialization;
using ElPaqueto.Authentication;
using ElPaqueto.Core;
using ElPaqueto.Web;
using ElPaqueto.Web.Helper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace ElPaqueto;

public static class IServiceCollectionExtensions
{
    public static IServiceCollection AddElPaquetoWebApplication(
        this IServiceCollection services,
        Action<ElPaquetoApplication> configureAction)
    {
        services
            .AddRouting(options => options.LowercaseUrls = true)
            .AddControllers()
            .AddApplicationPart(typeof(PackageContentController).Assembly)
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            });

        services.AddRazorPages();

        services.AddHttpContextAccessor();
        services.AddTransient<IUrlGenerator, ElPaquetoUrlGenerator>();

        services.AddSingleton(ApplicationVersionHelper.GetVersion());

        var app = services.AddElPaquetoApplication(configureAction);

        return services;
    }
}
