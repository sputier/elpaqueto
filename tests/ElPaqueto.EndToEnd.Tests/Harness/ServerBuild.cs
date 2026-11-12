namespace ElPaqueto.EndToEnd.Tests.Harness;

/// <summary>
/// Publishes the server once per test run, with <c>dotnet publish --no-build --no-restore</c>, into the run's
/// temporary directory. The server is built with the solution (the test project references it without
/// loading it), so publishing only copies the build output: it needs no network and writes nothing to the
/// user's NuGet folders.
/// </summary>
internal static class ServerBuild
{
    private static readonly Lazy<Task<string>> Published = new(() => PublishAsync(Repository.ServerProject));

    /// <summary>The directory that holds the published <c>ElPaqueto.dll</c> and its <c>appsettings.json</c>.</summary>
    public static Task<string> GetAsync() => Published.Value;

    /// <summary>Publishes a project. Throws an exception with an actionable message when anything is missing.</summary>
    public static async Task<string> PublishAsync(string project)
    {
        if (!File.Exists(project))
        {
            throw new InvalidOperationException($"Prerequisite missing: the server project '{project}' does not exist.");
        }

        var output = TempRoot.CreateDirectory("server");
        var result = await CommandRunner.RunAsync(new CommandSpec(
            CommandRunner.DotnetPath,
            ["publish", project, "--configuration", Repository.Configuration, "--no-build", "--no-restore", "--output", output, "--nologo"],
            Repository.Root,
            new Dictionary<string, string?>
            {
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_CLI_UI_LANGUAGE"] = "en",
                ["MSBUILDDISABLENODEREUSE"] = "1",
            },
            TimeSpan.FromMinutes(5)));

        if (result.TimedOut || result.ExitCode != 0 || !File.Exists(Path.Combine(output, "ElPaqueto.dll")))
        {
            throw new InvalidOperationException(
                $"Prerequisite failed: the server could not be published. Build the solution in the '{Repository.Configuration}' configuration first "
                + $"(dotnet build ElPaqueto.sln --configuration {Repository.Configuration})."
                + Environment.NewLine + result.Describe());
        }

        return output;
    }
}
