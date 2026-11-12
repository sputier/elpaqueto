using ElPaqueto.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Tests;

/// <summary>
/// The inherited BaGetter authentication settings are refused by the options validation that runs at startup
/// (issue #35), whatever the configuration source, without repeating their values.
/// </summary>
public sealed class LegacyAuthenticationSettingsTests : IDisposable
{
    private const string ValueMarker = "legacy-value-marker";

    private readonly string _secretsDirectory = Directory.CreateTempSubdirectory("elpaqueto-legacy-secrets-").FullName;

    public void Dispose() => Directory.Delete(_secretsDirectory, recursive: true);

    [Theory]
    [InlineData("ApiKey", "ApiKey")]
    [InlineData("Authentication:Credentials:0:Username", "Authentication:Credentials:0:Username")]
    [InlineData("Authentication:Credentials:0:Password", "Authentication:Credentials:0:Password")]
    [InlineData("Authentication:ApiKeys:0:Key", "Authentication:ApiKeys:0:Key")]
    [InlineData("authentication:apikeys:1:key", "Authentication:ApiKeys:1:key")]
    public void LegacySetting_FailsValidation_NamingThePathButNotTheValue(string key, string expected)
    {
        using var host = BuildHost(new Dictionary<string, string?> { [key] = ValueMarker });

        var exception = Assert.Throws<OptionsValidationException>(() => Options(host));

        var failure = Assert.Single(exception.Failures);
        Assert.Contains($"'{expected}'", failure, StringComparison.Ordinal);
        Assert.Contains("docs/user/upgrading-from-bagetter.md", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(ValueMarker, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacySettingsInSecretFiles_AreDetected()
    {
        File.WriteAllText(Path.Combine(_secretsDirectory, "ApiKey"), ValueMarker);
        File.WriteAllText(Path.Combine(_secretsDirectory, "Authentication__Credentials__0__Password"), ValueMarker);

        using var host = BuildHost(new Dictionary<string, string?>(), useSecretsDirectory: true);

        var exception = Assert.Throws<OptionsValidationException>(() => Options(host));

        Assert.Equal(2, exception.Failures.Count());
        Assert.Contains(exception.Failures, f => f.Contains("'ApiKey'", StringComparison.Ordinal));
        Assert.Contains(exception.Failures, f => f.Contains("'Authentication:Credentials:0:Password'", StringComparison.Ordinal));
        Assert.DoesNotContain(ValueMarker, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyValues_AreTreatedAsAbsent()
    {
        // "ApiKey": "" is what BaGetter shipped in its appsettings.json.
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["ApiKey"] = string.Empty,
            ["Authentication:Credentials:0:Username"] = string.Empty,
            ["Authentication:ApiKeys:0:Key"] = string.Empty,
        });

        Assert.NotNull(Options(host));
    }

    [Fact]
    public void UnrelatedAuthenticationSettings_AreAccepted()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            // The upstream feed's credentials, when mirroring: still supported.
            ["Mirror:Authentication:Type"] = "Basic",
            ["Mirror:Authentication:Username"] = "mirror-user",
            ["Mirror:Authentication:Password"] = ValueMarker,
            // ASP.NET Core's own section and the security module's settings are not BaGetter's.
            ["Authentication:DefaultScheme"] = "Other",
            ["Security:Authentication:VerificationCacheTtl"] = "00:00:30",
        });

        Assert.Equal("mirror-user", Options(host).Mirror.Authentication.Username);
    }

    [Fact]
    public void ShippedAppSettings_PassValidation()
    {
        var shipped = Path.Combine(SolutionRoot(), "src", "ElPaqueto", "appsettings.json");
        using var host = BuildHost(new Dictionary<string, string?>(), jsonFile: shipped);

        Assert.Empty(Configuration.LegacyAuthenticationSettingsValidator.FindLegacySettings(
            host.Services.GetRequiredService<IConfiguration>()));
        Assert.NotNull(Options(host));
    }

    private static ElPaquetoOptions Options(IHost host)
        => host.Services.GetRequiredService<IOptions<ElPaquetoOptions>>().Value;

    private IHost BuildHost(Dictionary<string, string?> values, bool useSecretsDirectory = false, string? jsonFile = null)
    {
        return new HostBuilder()
            .ConfigureAppConfiguration(config =>
            {
                if (jsonFile is not null)
                {
                    config.AddJsonFile(jsonFile, optional: false);
                }

                config.AddInMemoryCollection(values);

                if (useSecretsDirectory)
                {
                    config.AddKeyPerFile(_secretsDirectory, optional: false);
                }
            })
            .ConfigureServices(services =>
            {
                services.AddElPaquetoOptions<ElPaquetoOptions>();
                new ElPaquetoApplication(services).AddElPaquetoSecurity();
            })
            .Build();
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ElPaqueto.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
