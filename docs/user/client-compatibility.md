# Client compatibility

Which NuGet clients are tested against ElPaqueto, what is verified for each, and how. A client is listed as verified only when an automated test exercises it in CI on every pull request; everything else is **not verified**. That does not mean it fails: nobody has checked it yet.

## Matrix

| Client | Tested with | What is verified | How |
| --- | --- | --- | --- |
| `dotnet` CLI | The .NET SDK selected by [`global.json`](../../global.json), on `ubuntu-latest` and `windows-latest` | Adding the source with credentials (`NuGet.Config` or `NuGetPackageSourceCredentials_<name>`), listing it without the password; `dotnet nuget push` with an API key, with a symbol package, duplicates (409, `--skip-duplicate`); refused pushes (invalid, revoked or expired key, read-only key, owner without `Publish`, disabled owner, no key); `dotnet add package` with exact, latest, prerelease and floating versions; `dotnet restore` after clearing every cache, transitive dependencies, without credentials (401, no prompt); `dotnet build`; `dotnet test` with package source mapping; `dotnet package search`; unlist with `dotnet nuget delete`, hard delete; the 401, 403, 429 and 503 answers; persistence across a stop, a kill and a restart | `e2e` job of the `CI` workflow: [end-to-end tests](../technical/e2e-tests.md) in `tests/ElPaqueto.EndToEnd.Tests` |
| `dotnet` CLI against the container image | The .NET 10 SDK container, `linux/amd64` and `linux/arm64` | Push with a `Publish` key, restore with a password or a `Read` key after clearing the caches, refusals with the wrong key, revocation, restore after a container restart | `image (amd64)` and `image (arm64)` jobs of the `Docker` workflow: `eng/docker/smoke-test.sh` |
| NuGet client libraries (`NuGet.Protocol`) | The versions referenced by the test projects | Service index, search, metadata and package download through the client libraries, in process | `build-test` job of the `CI` workflow: `tests/ElPaqueto.Tests` |
| `nuget.exe` on Windows | | **Not verified** | Planned in [#43](https://github.com/sputier/ElPaqueto/issues/43) |
| `nuget.exe` on Mono (Linux, macOS) | | **Not verified** | |
| Visual Studio | | **Not verified** | Planned in [#43](https://github.com/sputier/ElPaqueto/issues/43) |
| JetBrains Rider, VS Code C# Dev Kit | | **Not verified** | |
| MSBuild-only builds (`msbuild -t:restore`), Paket, PowerShellGet | | **Not verified** | |
| Any client on macOS | | **Not verified** | No macOS job |
| Debuggers fetching symbols | | **Not verified** | |

## Transport

The end-to-end tests use plain HTTP on loopback, with `--allow-insecure-connections`: TLS is not exercised by any automated test. In production TLS is terminated by a reverse proxy or tunnel ([reverse proxy](reverse-proxy.md)); the `samples` job of the `Docker` workflow only checks the syntax of the nginx, Caddy, Traefik and Compose samples.

## Client behaviour to expect

The end-to-end tests assert how the `dotnet` CLI behaves, so a change in the client shows up as a failing test. The details are in [what the real clients do](../technical/e2e-tests.md#what-the-real-clients-do); the points that matter most to users:

- Every request is sent anonymously first, then again with the credentials after the `401` challenge.
- A restore with a wrong password fails with `NU1301` and `401` after six quick retries, without a prompt.
- A throttled (`429`) or busy (`503`) answer makes the client fail within seconds: it does not wait for `Retry-After`.
- `dotnet package search` exits with code 0 even when the source refuses it: scripts must check its output.
- An unlisted version is still chosen by a version range in restore; unlisting only hides it from search.

## Checking it yourself

```bash
dotnet build ElPaqueto.sln --configuration Release
dotnet test tests/ElPaqueto.EndToEnd.Tests --configuration Release --no-build --filter "Category=EndToEnd"

docker build -t elpaqueto:local .
bash eng/docker/smoke-test.sh elpaqueto:local
```

The end-to-end tests need Internet access for one test, which restores `xunit` from nuget.org. To report how another client behaves, open an issue with the client, its version, the operating system and the command used.
