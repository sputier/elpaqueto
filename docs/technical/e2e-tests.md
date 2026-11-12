# End-to-end tests

`tests/ElPaqueto.EndToEnd.Tests` runs the **published server as a child process** and drives it with the **real `dotnet` CLI** (`dotnet nuget push`, `dotnet add package`, `dotnet restore`, `dotnet build`, `dotnet test`, `dotnet nuget delete`, `dotnet package search`), with isolated NuGet folders and cleared caches. It checks what in-process tests cannot: how NuGet reads credentials from `NuGet.Config` or the environment, the plain-HTTP source check, the global packages folder and the HTTP cache, and the client's non-interactive behaviour.

Every test carries `[Trait("Category", "EndToEnd")]`. The `build-test` job of the `CI` workflow excludes them (`--filter "Category!=EndToEnd"`); the `e2e` job runs them on `ubuntu-latest` and `windows-latest`.

## Running them

```bash
dotnet build ElPaqueto.sln --configuration Release
dotnet test tests/ElPaqueto.EndToEnd.Tests --configuration Release --no-build
```

The server must be built first, in the same configuration as the tests: the harness publishes it with `dotnet publish --no-build --no-restore` (the test project references the server project without loading it, so building the tests builds the server). A full run takes about three to five minutes on a developer machine (test classes run two at a time). One test (#38 step 5) restores `Microsoft.NET.Test.Sdk` and `xunit` from nuget.org: the run needs Internet access. Nothing else needs the network, and no test needs a secret.

## The harness

| Part | File | What it does |
| --- | --- | --- |
| Server | `Harness/ServerBuild.cs`, `Harness/ServerProcess.cs` | Publishes the server once per run into the run's temporary directory, then starts `dotnet ElPaqueto.dll` with `ASPNETCORE_URLS=http://127.0.0.1:0` and fresh data (security database, package database, storage) in its own temporary directory. The port is read from the `Now listening on:` line; the server is ready when `/health` answers 200. It can be stopped (SIGTERM, see below), killed and restarted on the same port and data. Every setting is an environment variable; variables of the machine that could change the configuration (`Security__*`, `Database__*`, `ASPNETCORE_*`, `NUGET_*`, ...) are removed. |
| Server interface | `Harness/IServerUnderTest.cs` | What the scenarios use, so that a container-backed server (#46) can reuse them. |
| Identities | `Harness/AdminCli.cs` | Users and API keys are created by the real `admin` commands of the published server (`bootstrap`, `user create`, `user disable`, `user set-permissions`, `key create`, `key revoke`, `key show`, `user list`, `key list`). Passwords are random per run and written to standard input (`--password-stdin`), never passed as arguments; a key is read from the standard output of `admin key create`. |
| Client | `Harness/NuGetClient.cs` | Each client gets a fresh root holding `HOME`/`USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, `NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH`, `NUGET_SCRATCH`, `NUGET_PLUGINS_CACHE_PATH`, `DOTNET_CLI_HOME`, a `NuGet.Config` starting with `<clear />` and a copy of the repository's `global.json`. `NUGET_PLUGIN_PATHS` is empty, `DOTNET_CLI_UI_LANGUAGE=en` keeps the messages the tests read in English, build nodes and the compiler server are disabled so that no process outlives a command. Working directories are below that root, never in the repository. `--interactive` is never passed and standard input is closed. |
| Packages | `Harness/TestPackages.cs` | Generated once per run with the real `dotnet pack` from projects written by the harness, restored from a local folder (no network): `ElPaqueto.E2E.Base` 1.0.0, 1.1.0, 1.2.0-preview.1 and 2.0.0 (with a readme and a generated PNG icon), a second Base 1.0.0 with different content, `ElPaqueto.E2E.Dependent` 1.0.0 (depends on Base `[1.0.0, )`) and `ElPaqueto.E2E.Symbols` 1.0.0 with its `.snupkg`. No binary is committed. The packages carry no project, repository or licence URL, so that every URL in a server response is one the server generated. |
| Commands | `Harness/CommandRunner.cs` | Runs every child process with redirected streams and a timeout; on timeout the whole process tree is killed and the test fails. |
| Server-side checks | `Harness/ServerApi.cs` | Raw HTTP as a `Read` user (registration, `listed` flags, download counts, flat container) and the file system storage (paths and SHA-512). It also performs the setup steps no `dotnet` command expresses (relist). |
| Fixtures | `Harness/ServerFixture.cs` | One server per test class: start, `admin bootstrap`, a `reader`, a `publisher` and a Publish key, then the class's own settings and seed packages. Throttling limits are raised for every fixture except the one that observes throttling, because scenarios fail sign-ins on purpose. |

**Secrets.** Every generated password and key, and the Basic header value built from them, is registered in `Harness/Secrets.cs`; everything written to the test output goes through `Secrets.Redact`, which also masks anything shaped like an API key. Assertions that a secret does *not* appear (for example in `dotnet nuget list source`) read the raw, unredacted output. `ASecretFedThroughTheRunner_IsRedactedFromEverythingLogged` proves the redaction with a secret that really goes through a process.

**Isolation.** `EveryNuGetPathAndWorkingDirectory_IsUnderTheTemporaryRoot` asserts that `dotnet nuget locals all --list` resolves every folder below the client root, that the configuration files NuGet reads (`dotnet nuget config paths`) are the isolated ones plus, outside the temporary root, only machine-wide files (on Windows `%ProgramFiles(x86)%\NuGet\Config\*.config`, installed with Visual Studio; they are read-only and cannot be redirected), never a file of the user's profile or of the repository. The run's temporary directory (`epq-e2e-*` under the system temporary directory) is deleted when the test process exits; each fixture and client deletes its own directory when it is disposed, and each server process tree is killed, including after a failure.

**Missing prerequisites fail.** If the server cannot be published (not built, wrong configuration) or a package cannot be packed, every test fails with a message that starts with `Prerequisite` and says what to do. Nothing is skipped.

### Transport: plain HTTP on loopback

The harness uses `http://127.0.0.1:<port>`. The server allows plain HTTP from loopback (`Security:Transport`), and NuGet refuses plain-HTTP sources unless the source has `allowInsecureConnections="true"`: the harness adds every source with `dotnet nuget add source --allow-insecure-connections`, exactly as a user would. Trusting a throwaway certificate would need the OS trust store (a UI prompt or administrator rights on Windows, `SSL_CERT_FILE` on Linux) and cannot be done without side effects on a contributor's machine. **TLS itself is therefore not tested here**: it belongs to the reverse-proxy guidance and the validation report.

### Stopping the server

On Linux and macOS a stop is `kill -TERM`, and the test asserts exit code 0 and the `Application is shutting down` log line. On Windows, a console close signal cannot be sent to a child process without a console of its own without also reaching the test runner: the "graceful" persistence test kills the server there, and says so in its output (`Server stopped: Killed.`). The abrupt variant kills the server on every platform.

## Scenarios

| Issue | Class | What is covered |
| --- | --- | --- |
| #36 | `HarnessTests` | Smoke test (push with a Publish key, restore after clearing the caches), isolation of every path, redaction, a hung command killed at its timeout, cleanup of a server, a missing prerequisite. |
| #37 | `Scenarios/SourceAndPublishTests` | 1 `dotnet nuget add source` with credentials and `--allow-insecure-connections`; 2 `dotnet nuget list source` shows the source enabled and never the password; 3 credentials from `NuGetPackageSourceCredentials_elpaqueto`; 4 push with a Publish key; 5 push with a `.snupkg` through `SymbolPackagePublish/4.9.0`; 6 refused pushes (invalid, revoked, expired, read-only key, owner without Publish, disabled owner, no key) leave nothing in the registration or the storage; 7 a user holding Publish, with Basic credentials and no API key, gets 403. |
| #37 | `Scenarios/DuplicateAndVersioningTests` | 8 a second push of 1.0.0 with different content is refused with 409 and the stored bytes are unchanged; 9 `--skip-duplicate`; 10 `dotnet package search --exact-match` hides the prerelease unless `--prerelease`; 11–13 `dotnet add package` with an exact version, no version, `--prerelease`, read from `project.assets.json`; 14 `1.*` and `1.*-*`. |
| #38 | `Scenarios/ConsumeTests` | 1 `dotnet add package`; 2 restore after a verified cache clear resolves `Dependent` 1.0.0 and the transitive `Base` 1.0.0 from the server; 3 a second restore after another clear downloads again (the server's download counts increase by one); 4 `dotnet build --no-restore` of code using a type of the package; 5 `dotnet test` of an xunit project with package source mapping (`ElPaqueto.E2E.*` to ElPaqueto, `*` to nuget.org), asserting from the detailed restore log that no ElPaqueto package was requested from nuget.org; 6 the same restore without credentials fails with `NU1301`/401 without a prompt. |
| #39 | `Security/AuthorizationMatrixTests` | Rows 1–10 of the matrix (rows 6 and 8 reuse the refused pushes of #37). Row 9 runs with `PackageDeletionBehavior=HardDelete`: a Publish key cannot hard-delete (403, nothing deleted) and `/health/details` is 403 for every non-administrator credential. |
| #39 | `Security/ServiceIndexCrawlTests` | The crawl (below) and the forwarded-header spoofing variant. |
| #39, #99 | `Security/ClientStatusCodeTests` | What the client does with 401, 429 and 503 (below): a restore with a stale password ends with 401 and the right password works right after; distinct wrong passwords (sent with raw HTTP) still lead to 429. |
| #40 | `Scenarios/LifecycleTests` (`UnlistTests`, `HardDeleteTests`) | 1–6 unlist with `dotnet nuget delete`, hidden from search but still downloadable and restorable on an exact version and still resolved by a range (see below), relist with a raw `POST`, refusals, 404 only for an authorized caller; 7–8 hard delete with an Admin key removes the files and the restore fails, a Publish-only key gets 403 and the files stay. |
| #40 | `Scenarios/PersistenceTests` | 9–11 after a stop and a restart on the same data: identical `admin user list` and `admin key list` output, identical storage hashes, the unlisted version still unlisted, restore after a cleared cache, a new push with the Publish key, the revoked key and the disabled user still refused; 12 the same after a kill. |

### The crawl

With a `Read` user, the crawl reads `v3/index.json`, adds the URLs the V3 protocol derives from each resource (registration index; flat-container versions, `.nupkg`, `.nuspec` and readme; search and autocomplete queries) for `ElPaqueto.E2E.Base` 1.0.0 and 1.2.0-preview.1, then follows every absolute URL found in a JSON response (registration leaves, `packageContent`, icons, ...). It found 22 URLs in the last run; the test fails below 20. Every URL must have the origin of the server; every resource must answer 200 with `Read`; every URL, with GET and HEAD (PUT and GET for the publish endpoints), must answer 401 with the Basic challenge and an empty body without credentials and with a wrong password, and 403 with an empty body for a user without `Read`. The spoofing variant runs the server with a trusted proxy that is not loopback, sends `X-Forwarded-Host: attacker.example.com` and `X-Forwarded-Proto: https` from 127.0.0.1, and finds no generated URL with that host or scheme.

## What the real clients do

Recorded with the .NET SDK 10.0.401 (NuGet 7.9.0) on Windows 11 and, in CI, the SDK 10.0.404 that `global.json` rolls forward to on `ubuntu-latest` and `windows-latest`; the same counts were observed on both. The tests assert these facts, so a change in a client or in the server shows up as a failing test.

**Every request is sent twice.** NuGet sends each request (service index, flat container, `.nupkg`, push, delete) first without credentials, gets `401` with the Basic challenge, then sends it again with the source credentials. It never sends Basic credentials pre-emptively. The server log of any scenario shows the pairs.

| Server answer | What `dotnet` does (restore, push, search) |
| --- | --- |
| `401` to credentialed requests (wrong password, disabled user, revoked or expired key) | Retries: one `dotnet restore` sends **6 identical credentialed attempts** to the service index (12 requests with the anonymous ones) within a few seconds, then fails with `NU1301` and `Response status code does not indicate success: 401 (Unauthorized).` The server counts the six identical attempts as **one** failed sign-in ([repeated identical attempts](throttling.md#repeated-identical-attempts)): two such restores in a row still end with `401`, and a restore with the right password from the same address succeeds right after. No prompt: standard input is closed and `--interactive` is not set, and every refused command ends well within its timeout. `dotnet nuget push` and `dotnet nuget delete` report `401 (Unauthorized)` for a refused API key. |
| `403` | Fails with `403 (Forbidden)` in the output (`NU1301` for restore). Push with Basic credentials and no `--api-key` first warns `No API Key was provided`, then gets `403`. |
| `409` on push | Fails with `409 (Conflict)` and suggests `--skip-duplicate`; with it, exits 0 and reports that the package `already exists at feed`. |
| `429` | Fails with `429 (Too Many Requests)` after the same 6 attempts, about 8 seconds in all: **`Retry-After` (60 s) is not honoured**. The test trips the pair with five *distinct* wrong passwords sent with raw HTTP, then restores with the right password. Before [#99](https://github.com/sputier/ElPaqueto/issues/99), one restore with a stale password tripped the default `PairFailureLimit` (5) by itself, because its six identical attempts were counted one by one. |
| `503` with `Retry-After: 5` (no free verification slot) | Retries 6 times within about 6 seconds, without waiting for `Retry-After`, then fails with `503 (Service Unavailable)`. |

Other facts the tests rely on:

- `dotnet package search` exits with **0** when the source refuses it. In `--format json` the refusal is a `problems` entry (`Failed to retrieve metadata from source ...`) without the status code; in text output it is `error: Unable to load the service index for source ...`. Scripts must check the output, not the exit code.
- `dotnet add package` and `dotnet nuget delete` have no `--configfile` option: they read the `NuGet.Config` files above the working directory and the user-level one. `dotnet nuget delete` also reads the API key from `NUGET_API_KEY` when `--api-key` is missing (the harness removes that variable).
- **An unlisted version is still chosen for a version range.** Restore reads the versions from the flat container (`PackageBaseAddress`), which lists unlisted versions too, as upstream (and nuget.org's flat container) do; with `ElPaqueto.E2E.Base` 1.0.0 unlisted, `[1.0.0, )` still resolves to 1.0.0. Unlisting only hides a version from search (`dotnet package search`, the search resource) and keeps exact references working. Issue #40 expected 1.1.0; the test asserts what the client really does, and that the `listed` flag stays `false`.
- A hard delete removes the `.nupkg`, `.nuspec`, readme and icon of the version; the inherited file storage leaves the empty version folder behind.
- A second restore after `dotnet nuget locals all --clear` downloads the packages again: the download count of each version increases by exactly one per restore.

## Not covered

- `nuget.exe` and Visual Studio (#43): only the `dotnet` CLI is exercised.
- TLS (see above), containers (#46), and upgrades of the databases (#42).
- The symbol server download endpoints are reached by the crawl only when advertised; no debugger is exercised.
