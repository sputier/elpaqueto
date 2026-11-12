# Client setup

ElPaqueto works with the stock NuGet clients: the `dotnet` CLI, `nuget.exe` and the IDEs that use the same NuGet configuration. Every request needs credentials, so a client needs two things: the **source** (the URL of the service index) and **credentials** for it.

The commands on this page were run with the `dotnet` CLI of the .NET 10 SDK. Other clients and IDEs follow the same NuGet configuration rules but are not validated by this project yet (see [known limitations](known-limitations.md)).

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

## Plain HTTP

NuGet clients refuse plain-HTTP sources by default (`NU1302`) and warn about them (`NU1803`). ElPaqueto also refuses plain HTTP from any other machine. `allowInsecureConnections="true"` on a source (or `--allow-insecure-connections` on `dotnet nuget add source`) is only for a server on your own machine, as in the [quick start](../../readme.md#quick-start-docker-local-evaluation): credentials travel in clear.

## When it does not work

See [troubleshooting](troubleshooting.md) for `401`, `403`, `429` and the `NU1301` error.
