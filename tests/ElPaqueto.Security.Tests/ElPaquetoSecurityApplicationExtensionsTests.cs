using ElPaqueto.Core;
using Microsoft.Extensions.DependencyInjection;

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
    public void AddElPaquetoSecurity_RegistersNothingYet()
    {
        var services = new ServiceCollection();
        var app = new ElPaquetoApplication(services);

        app.AddElPaquetoSecurity();

        Assert.Empty(services);
    }

    [Fact]
    public void AddElPaquetoSecurity_RejectsNull()
    {
        ElPaquetoApplication app = null!;

        Assert.Throws<ArgumentNullException>(() => app.AddElPaquetoSecurity());
    }
}
