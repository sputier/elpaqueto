using System.Net;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ElPaqueto.Security.Tests;

/// <summary>
/// Starts the real, built host to check how it handles the configuration it is given at startup: the legacy
/// BaGetter settings and names (issues #35 and #59), a missing <c>HealthCheck</c> section (#92), and a
/// configuration file that cannot be read (#93).
/// </summary>
public sealed partial class StartupConfigurationProcessTests : IDisposable
{
    private const string ValueMarker = "legacy-value-marker";

    private readonly string _root = Directory.CreateTempSubdirectory("elpaqueto-startup-").FullName;

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A just-killed host may still hold a file for a moment: the temp directory is disposable.
        }
    }

    // ---- #35: legacy authentication settings ----------------------------------------------------------------

    [Theory]
    [InlineData("ApiKey", "ApiKey")]
    [InlineData("Authentication__Credentials__0__Username", "Authentication:Credentials:0:Username")]
    [InlineData("Authentication__Credentials__0__Password", "Authentication:Credentials:0:Password")]
    [InlineData("Authentication__ApiKeys__0__Key", "Authentication:ApiKeys:0:Key")]
    public async Task LegacyAuthenticationSetting_StopsTheServerBeforeItListens_NamingTheSettingButNotItsValue(string variable, string path)
    {
        var environment = DataEnvironment();
        environment[variable] = ValueMarker;

        using var host = HostProcess.Start([], environment);
        var exitCode = await host.WaitForExitAsync();

        Assert.Equal(1, exitCode);
        Assert.Contains($"The setting '{path}' comes from BaGetter", host.Output, StringComparison.Ordinal);
        Assert.Contains("upgrading-from-bagetter.md", host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(ValueMarker, host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Now listening", host.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyAuthenticationSetting_StopsAnAdminCommandBeforeItDoesAnything()
    {
        var environment = DataEnvironment();
        environment["ApiKey"] = ValueMarker;

        using var host = HostProcess.Start(["admin", "user", "list"], environment);
        var exitCode = await host.WaitForExitAsync();

        Assert.Equal(1, exitCode);
        Assert.Contains("The setting 'ApiKey' comes from BaGetter", host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(ValueMarker, host.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "auth.db")), "The command ran: it created the security database.");
    }

    [Fact]
    public async Task ShippedConfiguration_Starts_WithoutAnyUpgradeWarning()
    {
        using var host = HostProcess.Start([], DataEnvironment());

        await host.WaitForOutputAsync("Now listening");

        Assert.DoesNotContain("BaGetter", host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("BAGET_CONFIG_ROOT", host.Output, StringComparison.Ordinal);
    }

    // ---- #92: no HealthCheck section --------------------------------------------------------------------------

    [Fact]
    public async Task ConfigurationWithoutAHealthCheckSection_Starts_AndServesTheDefaultHealthPath()
    {
        // An empty configuration folder: no appsettings.json, everything else from environment variables.
        var configRoot = Directory.CreateDirectory(Path.Combine(_root, "empty-config")).FullName;
        var environment = DataEnvironment();
        environment["ELPAQUETO_CONFIG_ROOT"] = configRoot;
        environment["Database__Type"] = "Sqlite";
        environment["Storage__Type"] = "FileSystem";
        environment["Search__Type"] = "Database";
        environment["Mirror__Enabled"] = "false";

        using var host = HostProcess.Start([], environment);

        using var response = await GetAsync(host, "health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- #93: configuration that cannot be loaded -------------------------------------------------------------

    [Fact]
    public async Task MalformedConfigurationFile_ExitsWithCode1AndAMessage_WithoutQuotingTheFile()
    {
        var configRoot = Directory.CreateDirectory(Path.Combine(_root, "malformed-config")).FullName;
        await File.WriteAllTextAsync(Path.Combine(configRoot, "appsettings.json"), "{ \"Database\": \"" + ValueMarker + "\" ");

        using var host = HostProcess.Start([], new Dictionary<string, string> { ["ELPAQUETO_CONFIG_ROOT"] = configRoot });
        var exitCode = await host.WaitForExitAsync();

        Assert.True(exitCode == 1, $"Exit code {exitCode}. Output:{Environment.NewLine}{host.Output}");
        Assert.Contains("ElPaqueto could not load its configuration", host.Output, StringComparison.Ordinal);
        Assert.Contains("appsettings.json", host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(ValueMarker, host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", host.Output, StringComparison.Ordinal);
    }

    [UnprivilegedUnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task UnreadableConfigurationFile_ExitsWithCode1AndAMessage()
    {
        var configRoot = Directory.CreateDirectory(Path.Combine(_root, "unreadable-config")).FullName;
        var settings = Path.Combine(configRoot, "appsettings.json");
        await File.WriteAllTextAsync(settings, "{}");
        File.SetUnixFileMode(settings, UnixFileMode.None);

        try
        {
            using var host = HostProcess.Start([], new Dictionary<string, string> { ["ELPAQUETO_CONFIG_ROOT"] = configRoot });
            var exitCode = await host.WaitForExitAsync();

            Assert.True(exitCode == 1, $"Exit code {exitCode}. Output:{Environment.NewLine}{host.Output}");
            Assert.Contains("ElPaqueto could not load its configuration", host.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", host.Output, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(settings, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [UnprivilegedUnixFact]
    [UnsupportedOSPlatform("windows")]
    public void UnreadableSecretFile_FailsTheBuildWithAnExceptionThatMainReportsAsAConfigurationFailure()
    {
        // The server reads /run/secrets the same way; that folder cannot be used by a test.
        var secrets = Directory.CreateDirectory(Path.Combine(_root, "secrets")).FullName;
        var secret = Path.Combine(secrets, "Database__ConnectionString");
        File.WriteAllText(secret, ValueMarker);
        File.SetUnixFileMode(secret, UnixFileMode.None);

        try
        {
            var exception = Record.Exception(() => new ConfigurationBuilder()
                .AddKeyPerFile(secrets, optional: true)
                .Build());

            Assert.NotNull(exception);
            Assert.True(Program.IsConfigurationLoadFailure(exception), exception.GetType().FullName);
            Assert.DoesNotContain(ValueMarker, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(secret, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task UnconvertibleValue_ExitsWithCode1_WithoutPrintingTheValue()
    {
        // The configuration binder quotes a value it cannot convert in its exception message.
        var environment = DataEnvironment();
        environment["MaxPackageSizeGiB"] = ValueMarker;

        using var host = HostProcess.Start([], environment);
        var exitCode = await host.WaitForExitAsync();

        Assert.True(exitCode == 1, $"Exit code {exitCode}. Output:{Environment.NewLine}{host.Output}");
        Assert.Contains("ElPaqueto stopped because of an unhandled error", host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(ValueMarker, host.Output, StringComparison.Ordinal);
    }

    // ---- #59: upgrade from BaGetter ---------------------------------------------------------------------------

    [Fact]
    public async Task LegacyDatabaseNextToAMissingConfiguredFile_LogsOneWarningNamingBoth_AndStarts()
    {
        var legacy = Path.Combine(_root, "bagetter.db");
        await File.WriteAllBytesAsync(legacy, []);
        var configured = Path.Combine(_root, "elpaqueto.db");
        var environment = DataEnvironment();
        environment["Database__ConnectionString"] = $"Data Source={configured}";

        using var host = HostProcess.Start([], environment);
        await host.WaitForOutputAsync("Now listening");

        Assert.Single(LegacyDatabaseWarning().Matches(host.Output));
        Assert.Contains(configured, host.Output, StringComparison.Ordinal);
        Assert.Contains(legacy, host.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyConfigRootVariable_LoadsItsAppSettings_AndLogsADeprecationWarning()
    {
        var configRoot = Directory.CreateDirectory(Path.Combine(_root, "legacy-config")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(configRoot, "appsettings.json"),
            """
            {
              "Database": { "Type": "Sqlite" },
              "Storage": { "Type": "FileSystem" },
              "Search": { "Type": "Database" },
              "Mirror": { "Enabled": false },
              "HealthCheck": { "Path": "/from-legacy-root" }
            }
            """);
        var environment = DataEnvironment();
        environment["BAGET_CONFIG_ROOT"] = configRoot;
        environment["Logging__Console__LogLevel__Default"] = "Information";

        using var host = HostProcess.Start([], environment);
        using var response = await GetAsync(host, "from-legacy-root");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("BAGET_CONFIG_ROOT is deprecated", host.Output, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> GetAsync(HostProcess host, string relativePath)
    {
        await host.WaitForOutputAsync("Now listening");
        var address = ListeningAddress().Match(host.Output);
        Assert.True(address.Success, host.Output);

        using var client = new HttpClient { BaseAddress = new Uri(address.Groups[1].Value + "/") };
        return await client.GetAsync(new Uri(relativePath, UriKind.Relative));
    }

    private Dictionary<string, string> DataEnvironment() => new()
    {
        ["Security__Database__ConnectionString"] = $"Data Source={Path.Combine(_root, "auth.db")}",
        ["Database__ConnectionString"] = $"Data Source={Path.Combine(_root, "packages.db")}",
        ["Storage__Path"] = Path.Combine(_root, "storage"),
        ["Logging__Console__LogLevel__Default"] = "Information",
    };

    [GeneratedRegex(@"Now listening on: (http://127\.0\.0\.1:\d+)")]
    private static partial Regex ListeningAddress();

    [GeneratedRegex("but a BaGetter database")]
    private static partial Regex LegacyDatabaseWarning();
}
