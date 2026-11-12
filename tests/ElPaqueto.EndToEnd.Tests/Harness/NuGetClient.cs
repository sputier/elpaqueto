using System.Runtime.InteropServices;
using System.Text.Json;

namespace ElPaqueto.EndToEnd.Tests.Harness;

/// <summary>
/// The real <c>dotnet</c> CLI in an isolated environment. Each instance owns a fresh root below the run's
/// temporary directory, holding its own home, NuGet folders (global packages, HTTP cache, scratch, plugins
/// cache), CLI home, <c>NuGet.Config</c> (starting with <c>&lt;clear /&gt;</c>, passed with
/// <c>--configfile</c> where the command accepts it) and a <c>global.json</c> that pins the repository's SDK.
/// Working directories are created below that root, never in the repository.
/// </summary>
internal sealed class NuGetClient : IDisposable
{
    public const string SourceName = "elpaqueto";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(4);
    private static readonly Lazy<Task<string>> ToolVersions = new(ReadToolVersionsAsync);

    private readonly ITestOutputHelper? _output;
    private readonly Dictionary<string, string?> _environment;

    public NuGetClient(ITestOutputHelper? output)
    {
        _output = output;
        Root = TempRoot.CreateDirectory("c");
        HomeDirectory = Create("home");
        GlobalPackagesFolder = Create("pkgs");
        HttpCacheFolder = Create("http");
        ScratchFolder = Create("tmp");
        PluginsCacheFolder = Create("plug");
        var appData = Create("appdata");
        var localAppData = Create("local");
        var cliHome = Create("cli");
        ScratchTemp = Create("t");
        ConfigPath = Path.Combine(Root, "NuGet.Config");
        File.Copy(Repository.GlobalJson, Path.Combine(Root, "global.json"));
        WriteConfig("""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
              </packageSources>
            </configuration>
            """);

        _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // The user-level NuGet.Config, the default plugin folders and ~/.nuget are found from these.
            ["HOME"] = HomeDirectory,
            ["USERPROFILE"] = HomeDirectory,
            ["APPDATA"] = appData,
            ["LOCALAPPDATA"] = localAppData,
            ["XDG_CONFIG_HOME"] = Path.Combine(HomeDirectory, ".config"),
            ["XDG_DATA_HOME"] = Path.Combine(HomeDirectory, ".local", "share"),
            ["NUGET_PACKAGES"] = GlobalPackagesFolder,
            ["NUGET_HTTP_CACHE_PATH"] = HttpCacheFolder,
            ["NUGET_SCRATCH"] = ScratchFolder,
            ["NUGET_PLUGINS_CACHE_PATH"] = PluginsCacheFolder,
            // No credential provider may answer instead of NuGet.Config, nor prompt.
            ["NUGET_PLUGIN_PATHS"] = string.Empty,
            ["NUGET_NETCORE_PLUGIN_PATHS"] = string.Empty,
            ["DOTNET_CLI_HOME"] = cliHome,
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
            ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
            // Assertions read the client's messages: they must not be translated.
            ["DOTNET_CLI_UI_LANGUAGE"] = "en",
            // No build node or compiler server may outlive a command (and hold its output streams open).
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["UseSharedCompilation"] = "false",
            ["NUGET_XMLDOC_MODE"] = "skip",
            ["TMP"] = ScratchTemp,
            ["TEMP"] = ScratchTemp,
            ["TMPDIR"] = ScratchTemp,
            // The server is on loopback: never through a proxy configured on the machine.
            ["NO_PROXY"] = "127.0.0.1,localhost",
            ["no_proxy"] = "127.0.0.1,localhost",
        };
    }

    public string Root { get; }

    public string HomeDirectory { get; }

    public string GlobalPackagesFolder { get; }

    public string HttpCacheFolder { get; }

    public string ScratchFolder { get; }

    public string PluginsCacheFolder { get; }

    /// <summary>The temporary directory of every command (<c>TMP</c>, <c>TEMP</c>, <c>TMPDIR</c>).</summary>
    public string ScratchTemp { get; }

    /// <summary>The isolated <c>NuGet.Config</c>, at the client root so that commands without <c>--configfile</c> find it too.</summary>
    public string ConfigPath { get; }

    /// <summary>Every NuGet folder the client must keep below its root.</summary>
    public IReadOnlyList<string> NuGetFolders => [GlobalPackagesFolder, HttpCacheFolder, ScratchFolder, PluginsCacheFolder];

    /// <summary>The environment changes given to every command.</summary>
    public IReadOnlyDictionary<string, string?> Environment => _environment;

    /// <summary>Records the SDK and NuGet versions and the OS in the test output (read once per run).</summary>
    public async Task LogToolVersionsAsync()
        => _output?.WriteLine(await ToolVersions.Value);

    /// <summary>A new working directory below the client root.</summary>
    public string CreateWorkDirectory(string name)
    {
        var path = Path.Combine(Root, "w", name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Runs <c>dotnet</c> with the isolated environment, plus optional extra variables.</summary>
    public async Task<CommandResult> DotnetAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string?>? extraEnvironment = null)
    {
        var environment = new Dictionary<string, string?>(_environment);
        foreach (var (name, value) in extraEnvironment ?? new Dictionary<string, string?>())
        {
            environment[name] = value;
        }

        var result = await CommandRunner.RunAsync(new CommandSpec(
            CommandRunner.DotnetPath,
            arguments,
            workingDirectory,
            environment,
            timeout ?? DefaultTimeout));
        _output?.WriteLine(result.Describe());
        return result;
    }

    /// <summary>
    /// <c>dotnet nuget add source</c>, exactly as documented for users: plain HTTP is accepted for this source only
    /// (<c>--allow-insecure-connections</c>); credentials, when given, are stored in clear text in the isolated config.
    /// </summary>
    public Task<CommandResult> AddSourceAsync(Uri serviceIndex, UserCredential? credentials, string name = SourceName)
    {
        List<string> arguments = ["nuget", "add", "source", serviceIndex.AbsoluteUri, "--name", name];
        if (credentials is not null)
        {
            arguments.AddRange(["--username", credentials.UserName, "--password", credentials.Password, "--store-password-in-clear-text"]);
        }

        arguments.AddRange(["--allow-insecure-connections", "--configfile", ConfigPath]);
        return DotnetAsync(Root, arguments);
    }

    /// <summary>Replaces the isolated <c>NuGet.Config</c>.</summary>
    public void WriteConfig(string content) => File.WriteAllText(ConfigPath, content);

    /// <summary>
    /// <c>dotnet nuget locals all --clear</c>, then asserts that every isolated NuGet folder is empty, so that
    /// the next restore can only be served by the server.
    /// </summary>
    public async Task ClearCachesAsync()
    {
        (await DotnetAsync(Root, ["nuget", "locals", "all", "--clear"])).ShouldSucceed();
        foreach (var folder in NuGetFolders)
        {
            Assert.True(
                !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any(),
                $"{folder} is not empty after 'dotnet nuget locals all --clear'.");
        }
    }

    /// <summary>Pushes a package with <c>dotnet nuget push</c> to the configured source.</summary>
    public Task<CommandResult> PushAsync(TestPackage package, ApiKey? apiKey, params string[] extraArguments)
    {
        List<string> arguments = ["nuget", "push", package.Path, "--source", SourceName];
        if (apiKey is not null)
        {
            arguments.AddRange(["--api-key", apiKey.Token]);
        }

        arguments.AddRange(["--configfile", ConfigPath]);
        arguments.AddRange(extraArguments);
        return DotnetAsync(Root, arguments);
    }

    /// <summary>
    /// <c>dotnet nuget delete --non-interactive</c>. The command has no <c>--configfile</c>: run from the client root,
    /// it reads the isolated <c>NuGet.Config</c> there.
    /// </summary>
    public Task<CommandResult> DeleteAsync(string id, string version, ApiKey? apiKey)
    {
        List<string> arguments = ["nuget", "delete", id, version, "--source", SourceName, "--non-interactive"];
        if (apiKey is not null)
        {
            arguments.AddRange(["--api-key", apiKey.Token]);
        }

        return DotnetAsync(Root, arguments);
    }

    /// <summary>A <c>net10.0</c> class library project with the given package references and code.</summary>
    public string WriteProject(string name, IEnumerable<(string Id, string Version)> packageReferences, string? code = null)
    {
        var directory = CreateWorkDirectory(name);
        var references = string.Concat(packageReferences.Select(r => $"    <PackageReference Include=\"{r.Id}\" Version=\"{r.Version}\" />\n"));
        File.WriteAllText(Path.Combine(directory, name + ".csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
            {references}  </ItemGroup>
            </Project>
            """);
        if (code is not null)
        {
            File.WriteAllText(Path.Combine(directory, "Code.cs"), code);
        }

        return directory;
    }

    /// <summary>The <c>name/version</c> keys of the packages resolved in <c>obj/project.assets.json</c>.</summary>
    public static IReadOnlyList<string> ResolvedPackages(string projectDirectory)
    {
        var assets = Path.Combine(projectDirectory, "obj", "project.assets.json");
        Assert.True(File.Exists(assets), $"{assets} does not exist: the project was not restored.");
        using var json = JsonDocument.Parse(File.ReadAllText(assets));
        return json.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(p => p.Value.GetProperty("type").GetString() == "package")
            .Select(p => p.Name)
            .ToList();
    }

    /// <summary>The single resolved version of a package in <c>obj/project.assets.json</c>.</summary>
    public static string ResolvedVersion(string projectDirectory, string packageId)
    {
        var matches = ResolvedPackages(projectDirectory)
            .Where(p => p.StartsWith(packageId + "/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(matches.Count == 1, $"Expected one resolved version of {packageId}, found: {string.Join(", ", matches)}");
        return matches[0][(packageId.Length + 1)..];
    }

    /// <summary>The path of a package in the isolated global packages folder.</summary>
    public string GlobalPackagePath(string id, string version)
    {
        var lowerId = id.ToLowerInvariant();
        var lowerVersion = version.ToLowerInvariant();
        return Path.Combine(GlobalPackagesFolder, lowerId, lowerVersion, $"{lowerId}.{lowerVersion}.nupkg");
    }

    public void Dispose() => TempRoot.TryDelete(Root);

    private static async Task<string> ReadToolVersionsAsync()
    {
        using var client = new NuGetClient(output: null);
        try
        {
            var sdk = (await client.DotnetAsync(client.Root, ["--version"])).ShouldSucceed().StandardOutput.Trim();
            var nuget = (await client.DotnetAsync(client.Root, ["nuget", "--version"])).ShouldSucceed().StandardOutput.Trim();
            return $"Client: .NET SDK {sdk}; {nuget.ReplaceLineEndings(" ")}; OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
        }
        catch (Xunit.Sdk.XunitException e)
        {
            // Only a log line: a failure here must not fail every test.
            return "Client versions could not be read: " + Secrets.Redact(e.Message);
        }
    }

    private string Create(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
