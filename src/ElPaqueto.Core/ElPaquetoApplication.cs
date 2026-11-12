using System;
using Microsoft.Extensions.DependencyInjection;

namespace ElPaqueto.Core;

public class ElPaquetoApplication
{
    public ElPaquetoApplication(IServiceCollection services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public IServiceCollection Services { get; }
}
