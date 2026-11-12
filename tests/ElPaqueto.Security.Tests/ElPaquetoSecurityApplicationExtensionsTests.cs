using ElPaqueto.Core;
using ElPaqueto.Security.Configuration;
using ElPaqueto.Security.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Tests;

public class ElPaquetoSecurityApplicationExtensionsTests
{
    [Fact]
    public void AddElPaquetoSecurity_ReturnsTheSameApplication()
    {
        var app = new ElPaquetoApplication(new ServiceCollection());

        var result = app.AddElPaquetoSecurity();

        Assert.Same(app, result);
    }

    [Fact]
    public void AddElPaquetoSecurity_RegistersTheOptionsAndTheSecurityDatabase()
    {
        var services = new ServiceCollection();
        var app = new ElPaquetoApplication(services);

        app.AddElPaquetoSecurity();

        Assert.Contains(services, d => d.ServiceType == typeof(IConfigureOptions<ElPaquetoSecurityOptions>));
        Assert.Contains(services, d => d.ServiceType == typeof(IValidateOptions<ElPaquetoSecurityOptions>));
        Assert.Contains(services, d => d.ServiceType == typeof(AuthDbContext));
        Assert.Contains(services, d => d.ServiceType == typeof(IAuthDatabaseMigrator));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddElPaquetoSecurity_RejectsNull()
    {
        ElPaquetoApplication app = null!;

        Assert.Throws<ArgumentNullException>(() => app.AddElPaquetoSecurity());
    }
}
