namespace ElPaqueto.EndToEnd.Tests.Harness;

/// <summary>nuget.exe commands in the isolated environment of a <see cref="NuGetClient"/>.</summary>
internal static class NuGetExeHelpers
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Runs nuget.exe from the client root (below which every working directory is), with <c>-NonInteractive</c> (nothing
    /// may prompt) and <c>-ForceEnglishOutput</c> (the messages the tests read are not translated).
    /// </summary>
    public static Task<CommandResult> RunAsync(NuGetClient client, IReadOnlyList<string> arguments, string? workingDirectory = null, TimeSpan? timeout = null, IReadOnlyDictionary<string, string?>? environment = null)
        => RunRawAsync(client, [.. arguments, "-NonInteractive", "-ForceEnglishOutput"], workingDirectory, timeout, environment);

    /// <summary>Runs nuget.exe with exactly these arguments.</summary>
    public static async Task<CommandResult> RunRawAsync(NuGetClient client, IReadOnlyList<string> arguments, string? workingDirectory = null, TimeSpan? timeout = null, IReadOnlyDictionary<string, string?>? environment = null)
        => await client.RunAsync(await NuGetExe.GetPathAsync(), workingDirectory ?? client.Root, arguments, timeout ?? Timeout, environment);

    /// <summary>
    /// <c>nuget sources add</c> as documented for users: plain HTTP accepted for this source only
    /// (<c>-AllowInsecureConnections</c>, the loopback case of the tests), the password encrypted with DPAPI
    /// unless <paramref name="clearText"/>.
    /// </summary>
    public static Task<CommandResult> AddSourceAsync(NuGetClient client, Uri serviceIndex, UserCredential? credentials, bool clearText, string name = NuGetClient.SourceName, string? configFile = null)
    {
        List<string> arguments = ["sources", "add", "-Name", name, "-Source", serviceIndex.AbsoluteUri];
        if (credentials is not null)
        {
            arguments.AddRange(["-Username", credentials.UserName, "-Password", credentials.Password]);
            if (clearText)
            {
                arguments.Add("-StorePasswordInClearText");
            }
        }

        arguments.AddRange(["-AllowInsecureConnections", "-ConfigFile", configFile ?? client.ConfigPath]);
        return RunAsync(client, arguments);
    }

    /// <summary>Asserts that a configuration file contains (or not) a text, without ever printing a secret on failure.</summary>
    public static void AssertConfig(string config, string text, bool shouldContain)
    {
        var contains = config.Contains(text, StringComparison.Ordinal);
        Assert.True(
            contains == shouldContain,
            Secrets.Redact($"The configuration {(shouldContain ? "does not contain" : "contains")} '{text}'." + Environment.NewLine + config));
    }

    /// <summary>Writes the nuget.exe version (and the SHA-256 it was checked against) to the test output.</summary>
    public static async Task LogVersionAsync(NuGetClient client, ITestOutputHelper output)
    {
        var help = await RunAsync(client, ["help"]);
        var firstLine = help.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        output.WriteLine($"Client: {firstLine}; pinned release {NuGetExe.Version}, SHA-256 {NuGetExe.Sha256} verified; OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
    }
}
