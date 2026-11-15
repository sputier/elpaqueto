using ElPaqueto.Core;
using ElPaqueto.Security.Configuration;
using Microsoft.Extensions.Configuration;

namespace ElPaqueto.Security.Tests;

/// <summary>
/// Validation of <c>Security:Cors</c> and <c>Security:Hsts</c>.
/// </summary>
public class SecurityHeadersOptionsTests
{
    private static readonly ValidateElPaquetoOptions<ElPaquetoSecurityOptions> Validator = new("Security");

    [Fact]
    public void Defaults_AllowNoOriginAndNoHsts()
    {
        var options = new ElPaquetoSecurityOptions();

        Assert.Empty(options.Cors.AllowedOrigins);
        Assert.Empty(options.Cors.NormalizedOrigins);
        Assert.Equal(["GET", "HEAD"], options.Cors.EffectiveMethods);
        Assert.Equal(["Authorization"], options.Cors.EffectiveHeaders);
        Assert.False(options.Hsts.Enabled);
        Assert.Equal(31_536_000, options.Hsts.MaxAgeSeconds);
        Assert.False(options.Hsts.IncludeSubDomains);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.example.test")]
    [InlineData("null")]
    [InlineData("ui.example.test")]
    [InlineData("ftp://ui.example.test")]
    [InlineData("https://ui.example.test/app")]
    [InlineData("https://ui.example.test?x=1")]
    [InlineData("https://ui.example.test#top")]
    [InlineData("https://ui.example.test?")]
    [InlineData("https://user:secret@ui.example.test")]
    [InlineData("")]
    [InlineData("   ")]
    public void AnInvalidOrigin_IsRejectedAndNamedByIndex(string origin)
    {
        var options = new ElPaquetoSecurityOptions();
        options.Cors.AllowedOrigins = ["https://ok.example.test", origin];

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures!);
        Assert.Contains("Security:Cors:AllowedOrigins:1", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://ui.example.test", "https://ui.example.test")]
    [InlineData("https://ui.example.test/", "https://ui.example.test")]
    [InlineData("HTTPS://UI.Example.TEST", "https://ui.example.test")]
    [InlineData("https://ui.example.test:443", "https://ui.example.test")]
    [InlineData("http://ui.example.test:80", "http://ui.example.test")]
    [InlineData("https://ui.example.test:8443", "https://ui.example.test:8443")]
    [InlineData("http://localhost:3000", "http://localhost:3000")]
    [InlineData("http://[::1]:3000", "http://[::1]:3000")]
    [InlineData("https://bücher.example.test", "https://xn--bcher-kva.example.test")]
    public void AValidOrigin_IsNormalizedToTheFormBrowsersSend(string configured, string expected)
    {
        var options = new ElPaquetoSecurityOptions();
        options.Cors.AllowedOrigins = [configured];

        Assert.True(Validator.Validate(name: null, options).Succeeded);
        Assert.Equal([expected], options.Cors.NormalizedOrigins);
    }

    [Theory]
    [InlineData("OPTIONS")]
    [InlineData("PATCH")]
    [InlineData("TRACE")]
    [InlineData("*")]
    [InlineData("")]
    public void AMethodTheServerDoesNotUse_IsRejected(string method)
    {
        var options = new ElPaquetoSecurityOptions();
        options.Cors.AllowedMethods = [method];

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Security:Cors:AllowedMethods:0", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfiguredMethods_AreNormalizedAndReplaceTheDefault()
    {
        var options = new ElPaquetoSecurityOptions();
        options.Cors.AllowedMethods = ["get", " Put "];

        Assert.True(Validator.Validate(name: null, options).Succeeded);
        Assert.Equal(["GET", "PUT"], options.Cors.EffectiveMethods);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("")]
    [InlineData("Bad Header")]
    [InlineData("Bad:Header")]
    public void AnInvalidHeaderName_IsRejected(string header)
    {
        var options = new ElPaquetoSecurityOptions();
        options.Cors.AllowedHeaders = [header];

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Security:Cors:AllowedHeaders:0", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfiguredHeaders_ReplaceTheDefault()
    {
        var options = new ElPaquetoSecurityOptions();
        options.Cors.AllowedHeaders = ["X-NuGet-ApiKey"];

        Assert.True(Validator.Validate(name: null, options).Succeeded);
        Assert.Equal(["X-NuGet-ApiKey"], options.Cors.EffectiveHeaders);
    }

    [Fact]
    public void BindingAListOfMethods_DoesNotKeepTheDefaultMethods()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cors:AllowedMethods:0"] = "PUT" })
            .Build();
        var options = new ElPaquetoSecurityOptions();

        configuration.Bind(options);

        Assert.Equal(["PUT"], options.Cors.EffectiveMethods);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(SecurityHstsOptions.MaximumMaxAgeSeconds + 1)]
    public void HstsMaxAgeOutsideTheAcceptedRange_IsRejected(int maxAge)
    {
        var options = new ElPaquetoSecurityOptions();
        options.Hsts.MaxAgeSeconds = maxAge;

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Security:Hsts:MaxAgeSeconds", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(SecurityHstsOptions.MaximumMaxAgeSeconds)]
    public void HstsMaxAgeWithinTheAcceptedRange_IsAccepted(int maxAge)
    {
        var options = new ElPaquetoSecurityOptions();
        options.Hsts.MaxAgeSeconds = maxAge;

        Assert.True(Validator.Validate(name: null, options).Succeeded);
    }
}
