using ElPaqueto.Core;
using ElPaqueto.Security.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Tests;

/// <summary>
/// Environment variables are process-wide: these tests must not run in parallel with each other.
/// </summary>
[Collection("EnvironmentVariables")]
public sealed class SecurityConfigurationHostTests : IDisposable
{
    private const string EnvironmentVariable = "Security__Database__ConnectionString";

    private readonly string? _previousValue = Environment.GetEnvironmentVariable(EnvironmentVariable);
    private readonly string _secretsDirectory = Directory.CreateTempSubdirectory("elpaqueto-secrets-").FullName;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvironmentVariable, _previousValue);
        Directory.Delete(_secretsDirectory, recursive: true);
    }

    [Fact]
    public void ValidConfiguration_PassesValidation()
    {
        using var host = BuildHost(out var logs, new Dictionary<string, string?>
        {
            ["Security:Database:ConnectionString"] = "Data Source=/data/db/auth.db",
        });

        Assert.True(host.ValidateElPaquetoSecurityOptions());
        Assert.Empty(logs.Messages);
    }

    [Fact]
    public void AbsentSection_UsesDefaultsAndPasses()
    {
        using var host = BuildHost(out _, new Dictionary<string, string?>());

        Assert.True(host.ValidateElPaquetoSecurityOptions());
        Assert.Equal(
            "Data Source=auth.db",
            host.Services.GetRequiredService<IOptions<ElPaquetoSecurityOptions>>().Value.Database.ConnectionString);
    }

    [Fact]
    public void EmptyConnectionString_FailsAndLogsTheSettingName()
    {
        using var host = BuildHost(out var logs, new Dictionary<string, string?>
        {
            ["Security:Database:ConnectionString"] = "   ",
        });

        Assert.False(host.ValidateElPaquetoSecurityOptions());
        Assert.Contains(logs.Messages, m => m.Contains("Security:Database:ConnectionString", StringComparison.Ordinal));
    }

    [Fact]
    public void FailureLogs_NeverContainTheConfiguredValue()
    {
        const string secretMarker = "s3cr3t-marker";
        using var host = BuildHost(out var logs, new Dictionary<string, string?>
        {
            ["Security:Database:ConnectionString"] = secretMarker + new string('x', SecurityDatabaseOptions.MaxConnectionStringLength),
        });

        Assert.False(host.ValidateElPaquetoSecurityOptions());
        Assert.NotEmpty(logs.Messages);
        Assert.All(logs.Messages, m => Assert.DoesNotContain(secretMarker, m, StringComparison.Ordinal));
    }

    [Fact]
    public void EnvironmentVariable_BindsTheSetting()
    {
        Environment.SetEnvironmentVariable(EnvironmentVariable, "Data Source=from-environment.db");

        using var host = BuildHost(out _, new Dictionary<string, string?>(), useEnvironment: true);

        Assert.True(host.ValidateElPaquetoSecurityOptions());
        Assert.Equal(
            "Data Source=from-environment.db",
            host.Services.GetRequiredService<IOptions<ElPaquetoSecurityOptions>>().Value.Database.ConnectionString);
    }

    [Fact]
    public void SecretFile_BindsTheSettingAndWinsOverTheEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(EnvironmentVariable, "Data Source=from-environment.db");
        File.WriteAllText(Path.Combine(_secretsDirectory, EnvironmentVariable), "Data Source=from-secret-file.db");

        using var host = BuildHost(out _, new Dictionary<string, string?>(), useEnvironment: true, useSecretsDirectory: true);

        Assert.True(host.ValidateElPaquetoSecurityOptions());
        Assert.Equal(
            "Data Source=from-secret-file.db",
            host.Services.GetRequiredService<IOptions<ElPaquetoSecurityOptions>>().Value.Database.ConnectionString);
    }

    [Fact]
    public void UnconvertibleValue_FailsWithAFixedMessageThatNeverRepeatsTheValue()
    {
        const string secretMarker = "s3cr3t-marker";
        using var host = BuildHost(
            out var logs,
            new Dictionary<string, string?>(),
            configureServices: services => services.AddSingleton<IConfigureOptions<ElPaquetoSecurityOptions>>(
                new ThrowingConfigureOptions($"Failed to convert configuration value '{secretMarker}'")));

        Assert.False(host.ValidateElPaquetoSecurityOptions());
        Assert.NotEmpty(logs.Messages);
        Assert.All(logs.Messages, m => Assert.DoesNotContain(secretMarker, m, StringComparison.Ordinal));
        Assert.Contains(logs.Messages, m => m.Contains("cannot be converted", StringComparison.Ordinal));
    }

    [Fact]
    public void ModuleNotRegistered_IsReportedAsInvalid()
    {
        using var host = BuildHost(out var logs, new Dictionary<string, string?>(), registerModule: false);

        Assert.False(host.ValidateElPaquetoSecurityOptions());
        Assert.Contains(logs.Messages, m => m.Contains("AddElPaquetoSecurity", StringComparison.Ordinal));
    }

    [Fact]
    public void PlainValueWhereASectionIsExpected_IsRejected()
    {
        using var host = BuildHost(out var logs, new Dictionary<string, string?>
        {
            ["Security:Database"] = "Data Source=/data/db/auth.db",
        });

        Assert.False(host.ValidateElPaquetoSecurityOptions());
        Assert.Contains(logs.Messages, m => m.Contains("Security:Database is a section", StringComparison.Ordinal));
        Assert.All(logs.Messages, m => Assert.DoesNotContain("/data/db/auth.db", m, StringComparison.Ordinal));
    }

    [Fact]
    public void PlainValueWhereThePasswordsSectionIsExpected_IsRejected()
    {
        using var host = BuildHost(out var logs, new Dictionary<string, string?>
        {
            ["Security:Passwords"] = "210000",
        });

        Assert.False(host.ValidateElPaquetoSecurityOptions());
        Assert.Contains(logs.Messages, m => m.Contains("Security:Passwords", StringComparison.Ordinal));
    }

    [Fact]
    public void NonNumericIterationCount_FailsWithoutEchoingTheValue()
    {
        const string secretMarker = "s3cr3t-marker";
        using var host = BuildHost(out var logs, new Dictionary<string, string?>
        {
            ["Security:Passwords:IterationCount"] = secretMarker,
        });

        Assert.False(host.ValidateElPaquetoSecurityOptions());
        Assert.NotEmpty(logs.Messages);
        Assert.All(logs.Messages, m => Assert.DoesNotContain(secretMarker, m, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Security:ReverseProxy:KnownNetworks:0", "0.0.0.0/0", "Security:ReverseProxy:KnownNetworks:0")]
    [InlineData("Security:ReverseProxy:KnownNetworks:0", "::/0", "Security:ReverseProxy:KnownNetworks:0")]
    [InlineData("Security:ReverseProxy:KnownProxies:0", "not-an-address", "Security:ReverseProxy:KnownProxies:0")]
    [InlineData("Security:ReverseProxy:ForwardLimit", "0", "Security:ReverseProxy:ForwardLimit")]
    [InlineData("Security:PublicBaseUrl", "http://nuget.example.test", "Security:PublicBaseUrl")]
    [InlineData("Security:PublicBaseUrl", "nuget.example.test", "Security:PublicBaseUrl")]
    [InlineData("Security:ReverseProxy", "198.51.100.0/24", "Security:ReverseProxy is a section")]
    [InlineData("Security:Transport", "false", "Security:Transport is a section")]
    [InlineData("Security:ReverseProxy:KnownProxies", "198.51.100.1", "Security:ReverseProxy:KnownProxies is a list")]
    [InlineData("Security:ReverseProxy:KnownNetworks", "198.51.100.0/24", "Security:ReverseProxy:KnownNetworks is a list")]
    public void InvalidProxyOrTransportSetting_FailsAndNamesTheSetting(string key, string value, string expected)
    {
        using var host = BuildHost(out var logs, new Dictionary<string, string?> { [key] = value });

        Assert.False(host.ValidateElPaquetoSecurityOptions());
        Assert.Contains(logs.Messages, m => m.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void ValidProxyAndTransportSettings_PassValidation()
    {
        using var host = BuildHost(out var logs, new Dictionary<string, string?>
        {
            ["Security:ReverseProxy:KnownProxies:0"] = "192.0.2.10",
            ["Security:ReverseProxy:KnownNetworks:0"] = "203.0.113.0/24",
            ["Security:ReverseProxy:KnownNetworks:1"] = "2001:db8::/32",
            ["Security:ReverseProxy:ForwardLimit"] = "2",
            ["Security:PublicBaseUrl"] = "https://nuget.example.test",
            ["Security:Transport:RequireHttps"] = "true",
        });

        Assert.True(host.ValidateElPaquetoSecurityOptions());
        Assert.Empty(logs.Messages);

        var options = host.Services.GetRequiredService<IOptions<ElPaquetoSecurityOptions>>().Value;
        Assert.Equal(["192.0.2.10"], options.ReverseProxy.KnownProxies);
        Assert.Equal(["203.0.113.0/24", "2001:db8::/32"], options.ReverseProxy.KnownNetworks);
        Assert.Equal(2, options.ReverseProxy.ForwardLimit);
    }

    private IHost BuildHost(
        out LogCollector logs,
        Dictionary<string, string?> values,
        bool useEnvironment = false,
        bool useSecretsDirectory = false,
        bool registerModule = true,
        Action<IServiceCollection>? configureServices = null)
    {
        var collector = new LogCollector();
        logs = collector;

        return new HostBuilder()
            .ConfigureAppConfiguration(config =>
            {
                config.AddInMemoryCollection(values);

                if (useEnvironment)
                {
                    config.AddEnvironmentVariables();
                }

                // Same order as the server: the secrets directory is added last and wins.
                if (useSecretsDirectory)
                {
                    config.AddKeyPerFile(_secretsDirectory, optional: false);
                }
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(collector);
            })
            .ConfigureServices(services =>
            {
                if (registerModule)
                {
                    new ElPaquetoApplication(services).AddElPaquetoSecurity();
                }

                configureServices?.Invoke(services);
            })
            .Build();
    }

    private sealed class ThrowingConfigureOptions(string message) : IConfigureOptions<ElPaquetoSecurityOptions>
    {
        public void Configure(ElPaquetoSecurityOptions options) => throw new InvalidOperationException(message);
    }

    private sealed class LogCollector : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class CollectingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var text = formatter(state, exception) + (exception is null ? string.Empty : " " + exception);

                lock (messages)
                {
                    messages.Add(text);
                }
            }
        }
    }
}
