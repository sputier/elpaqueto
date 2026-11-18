# Client setup

ElPaqueto is meant for the stock NuGet clients, with no plugin or credential provider. Every request needs credentials, so a client needs two things: the **source** (the URL of the service index) and **credentials** for it.

The commands on this page use the `dotnet` CLI of the .NET 10 SDK, and the [nuget.exe](#nuget-exe), [Visual Studio](#visual-studio) and [JetBrains Rider](#jetbrains-rider) sections follow. The `dotnet` CLI and `nuget.exe` (and the `msbuild -t:restore` that Visual Studio runs) are tested automatically; the IDEs' own windows are not, and a [manual checklist](client-checklist.md) covers them. What is verified, and how, is in [client compatibility](client-compatibility.md).

## Which credential for what

| Operation | Credential | Sent as |
| --- | --- | --- |
| Restore, search, browse, read the service index | A user's password, or an API key with `Read` | HTTP Basic, the source credentials of `NuGet.Config` |
| Push, symbol push, delete or unlist, relist | An API key with `Publish` | `X-NuGet-ApiKey`, from `--api-key` |
| Web UI | A user's password (or a `Read` key) | The browser's own credential prompt |

With a key as the Basic password, the user name can be anything non-empty: it is ignored, and `apikey` is the convention. A password or a key sent as a Basic password is **refused for writes** (403), even when it holds `Publish`. Details: [Using an API key](configuration.md#using-an-api-key).

Prefer API keys over passwords for machines: a key holds only the permissions it needs, can expire, and is revoked without touching the user's password. Create keys with [`admin key create`](administration.md#api-keys).

## The source

A `NuGet.Config` committed with your code names the source and never contains a secret:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="elpaqueto" value="https://nuget.example.test/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
    <packageSource key="elpaqueto">
      <package pattern="Contoso.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

[Package source mapping](https://learn.microsoft.com/nuget/consume-packages/package-source-mapping) sends each package ID to one source: your own prefix (`Contoso.*` here) to ElPaqueto, everything else to nuget.org. Without it, the client asks every source for every package, which exposes your internal package names to nuget.org and lets a public package with the same name win. When ElPaqueto mirrors nuget.org (`Mirror:Enabled`), you can list ElPaqueto alone instead.

The source name (`elpaqueto`) is what the credentials below refer to.

## Credentials in CI

Give the credentials through the `NuGetPackageSourceCredentials_<source name>` environment variable, set by the CI system from its secret store. `dotnet restore` and `dotnet nuget push` read it:

```bash
export NuGetPackageSourceCredentials_elpaqueto="Username=apikey;Password=$ELPAQUETO_READ_KEY"
dotnet restore
dotnet nuget push artifacts/*.nupkg --source elpaqueto --api-key "$ELPAQUETO_PUBLISH_KEY"
```

Use a `Read` key for the source credentials of restore jobs and a separate `Publish` key for `--api-key` in publishing jobs. `dotnet nuget push` reads the service index with the source credentials before pushing, so a publishing job needs both (one key holding `read` and `publish` also works). Mask both values in the CI log.

## Credentials on a workstation

Keep them in your **user-level** `NuGet.Config` (`%AppData%\NuGet\NuGet.Config` on Windows, `~/.nuget/NuGet/NuGet.Config` on Linux and macOS), never in the repository's file. Do not pass a password or a key as a command-line argument: other users of the machine can see process arguments, and the shell keeps them in its history. `dotnet nuget add source --password` and `dotnet nuget update source --password` take the secret that way, so prefer one of these:

- **For a terminal session**, the same environment variable as in CI, read from a file only you can read:

  ```bash
  export NuGetPackageSourceCredentials_elpaqueto="Username=apikey;Password=$(cat ~/.elpaqueto.key)"
  ```

- **Persistently**, add a `packageSourceCredentials` section to your user-level `NuGet.Config` with a text editor. The element name (`elpaqueto`) must match the name of the source:

  ```xml
  <configuration>
    <packageSourceCredentials>
      <elpaqueto>
        <add key="Username" value="apikey" />
        <add key="ClearTextPassword" value="<key>" />
      </elpaqueto>
    </packageSourceCredentials>
  </configuration>
  ```

  The value is stored as written: restrict the file to your account (`chmod 600 ~/.nuget/NuGet/NuGet.Config` on Linux and macOS).

On Windows, the client can instead store the value encrypted for the current user with DPAPI (`Password` instead of `ClearTextPassword`), but the CLI only writes it from `dotnet nuget update source <name> --username apikey --password <key> --configfile <user-level file>`, with the key on that command line. On Linux and macOS there is no encryption, and the same command requires `--store-password-in-clear-text`.

[Credential providers](https://learn.microsoft.com/nuget/reference/extensibility/nuget-cross-platform-authentication-plugin) are optional: ElPaqueto needs none, and no ElPaqueto-specific provider exists.

## nuget.exe

Tested with nuget.exe 7.9.0 on Windows. Add the source once; nuget.exe writes the credentials to the user-level `%AppData%\NuGet\NuGet.Config`:

```bat
nuget sources add -Name elpaqueto -Source https://nuget.example.test/v3/index.json -Username apikey -Password <read key>
```

Without `-StorePasswordInClearText` the password is **encrypted for the current Windows user** (DPAPI): the file holds a `Password` entry, which only that user on that machine can decrypt. That is the form to keep. `-StorePasswordInClearText` writes `ClearTextPassword`: use it only for a throwaway configuration. nuget.exe has no way to read the secret from a file: `-Password` is a command-line argument, visible to other users of the machine while it runs and kept in the shell history. Type the command in a session you can then clear, or add the `packageSourceCredentials` section by hand (see above), or use the [`NuGetPackageSourceCredentials_elpaqueto`](#credentials-in-ci) variable, which nuget.exe also reads.

```bat
nuget search Contoso.Core -Source elpaqueto -NonInteractive
nuget install Contoso.Core -Version 1.0.0 -OutputDirectory packages -Source elpaqueto -NonInteractive
nuget restore MyApp.sln -NonInteractive
nuget push Contoso.Core.1.0.0.nupkg -Source elpaqueto -ApiKey <publish key> -NonInteractive
nuget delete Contoso.Core 1.0.0 -Source elpaqueto -ApiKey <publish key> -NonInteractive
```

- **The API key for push and delete** goes in `-ApiKey`: nuget.exe sends it as `X-NuGet-ApiKey`, the header ElPaqueto requires for writes. The source credentials only read the service index.- **Plain HTTP** (a server on your own machine): add `-AllowInsecureConnections` to `sources add` **and to every later `sources update`**: an update without it silently removes the setting, and the next command fails with "NuGet requires HTTPS sources".
- **`nuget search`, not `nuget list`.** `list` is deprecated and answers `This version of nuget.exe does not support listing packages from package source` for a V3 source, with exit code 0 and no package. `search` does not accept `-ConfigFile`: it reads the `NuGet.Config` files of the working directory and the user-level one. `nuget search` shows the latest version; add `-PreRelease` for prerelease versions.
- **`-NonInteractive`** keeps scripts from waiting for input. Without it, nuget.exe still never waits when the credentials are refused: it retries for about twelve seconds and fails with `401` (see [client compatibility](client-compatibility.md)). Add `-ForceEnglishOutput` if your scripts read the messages.
- `nuget restore` of a `packages.config` project downloads the packages to the solution's `packages` folder, and also to the global packages folder.

## Visual Studio

Visual Studio restores through NuGet's MSBuild restore, the same one as `msbuild -t:restore`, with the same configuration as nuget.exe, so everything above about `NuGet.Config` applies. Add the source under **Tools > Options > NuGet Package Manager > Package Sources** with the URL `https://nuget.example.test/v3/index.json`.

- **Credentials.** Visual Studio asks for a user name and password in a dialog the first time the source answers `401`, and stores them (encrypted) for you. Any user name works with an API key as the password. You can also put them in the user-level `NuGet.Config` ahead of time, or in a `NuGet.Config` next to the solution; **never commit a file with a secret**: keep the solution's file to the `packageSources`, and the credentials in the user-level file (the committed-file pattern is in [the source](#the-source)).
- **Readme tab.** Visual Studio 2022 (17.13 and later) and 2026 show a package's readme only if the service index advertises `ReadmeUriTemplate/6.13.0`. ElPaqueto does (upstream BaGetter did not, [bagetter/BaGetter#289](https://github.com/bagetter/BaGetter/issues/289)); the end-to-end tests read the template from the service index and fetch a readme with it.
- **What is tested.** `msbuild -t:restore` with the Visual Studio MSBuild installed on the test machine, with credentials encrypted by nuget.exe (what Visual Studio writes), with clear-text credentials and with none (`NU1301`, `401`, no prompt). The Package Manager window, the credential dialog and the readme tab are **not** tested automatically: follow the [manual checklist](client-checklist.md).

## JetBrains Rider

Rider uses the NuGet client libraries and the same `NuGet.Config` files. Add the source under **Settings > Build, Execution, Deployment > NuGet > Sources** (the URL and, in the same dialog, the user name and password), or put the source and its credentials in your user-level `NuGet.Config` as above, and restore from the NuGet tool window or with `dotnet restore`. Rider has not been tested against ElPaqueto, automatically or by hand: the [checklist](client-checklist.md) lists what to check, and in particular where Rider stores the password you type (look at the user-level `NuGet.Config`: it may be clear text). For a project, `dotnet restore` in Rider's terminal is the tested path.

## Plain HTTP

NuGet clients refuse plain-HTTP sources by default (`NU1302`) and warn about them (`NU1803`). ElPaqueto also refuses plain HTTP from any other machine. `allowInsecureConnections="true"` on a source (or `--allow-insecure-connections` on `dotnet nuget add source`) is only for a server on your own machine, as in the [quick start](../../readme.md#quick-start-docker-local-evaluation): credentials travel in clear.

## When it does not work

See [troubleshooting](troubleshooting.md) for `401`, `403`, `429` and the `NU1301` error.
