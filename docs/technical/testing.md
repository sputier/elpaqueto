# Testing

## Levels

| Level | Where | What it covers | Runs in |
| --- | --- | --- | --- |
| Unit | `tests/ElPaqueto.Security.Tests` | The security module: options validation, password hashing, API key format and service, throttling, credential parsing, CLI commands, audit catalogue, module boundary, documentation coverage of the `Security` settings. | `dotnet test`, CI |
| Process | `tests/ElPaqueto.Security.Tests` (`*ProcessTests`, `HostProcess`) | The real server or `admin` command as a separate process: exit codes, startup failures, configuration loading, standard output and error. Some are Unix-only (`UnixFact`), for file modes and signals. | `dotnet test`, CI on Linux and Windows |
| Inherited unit | `tests/ElPaqueto.Core.Tests`, `tests/ElPaqueto.Protocol.Tests`, `tests/ElPaqueto.Web.Tests` | Inherited BaGetter tests, kept, plus the safe Markdown rendering of package READMEs. | `dotnet test`, CI |
| Integration | `tests/ElPaqueto.Tests` | The whole pipeline in-process (`ElPaquetoApplication`, a `WebApplicationFactory` with its own security database): authentication, the authorization matrix and endpoint inventory, API keys, throttling, transport and proxies, CORS, headers, audit logging, the web UI and its branding, the NuGet client libraries against the host, BaGetter upgrade checks. | `dotnet test`, CI |
| End to end | `tests/ElPaqueto.EndToEnd.Tests` (`Category=EndToEnd`) | The published server as a child process, driven by the real `dotnet` CLI with isolated NuGet folders: source setup, push, add package, restore, build, test, search, unlist and delete, the authorization matrix, the service index crawl, client behaviour on 401/403/429/503, restart persistence; on Windows also the real nuget.exe and `msbuild -t:restore`. See [end-to-end tests](e2e-tests.md). | CI `e2e` job on Linux and Windows |
| PostgreSQL | `tests/ElPaqueto.Tests/PostgreSqlIntegrationTests.cs` (`Category=PostgreSql`), `eng/docker/compose-postgres-smoke.sh` | The real host (`ElPaquetoApplication` with `Database:Type=PostgreSql`) against a PostgreSQL 17 container started by Testcontainers: migrations (empty, repeated, concurrent, repaired `Version` column), push, restore, search, metadata, unlist, relist, hard delete, case handling, restart, concurrency on a small connection pool, health. The script brings up `samples/compose-postgres` (PostgreSQL, ElPaqueto, Caddy over TLS) and drives it with the admin commands and the `dotnet` CLI. | CI `postgresql` job (Linux); locally with Docker. Without a Docker daemon the tests are **skipped with the reason**; `ELPAQUETO_REQUIRE_DOCKER=1` turns that into a failure (the CI job sets it). |
| Google Cloud Storage | `tests/ElPaqueto.Tests/GoogleCloudStorageIntegrationTests.cs` (`Category=Gcs`) | The storage provider against `fsouza/fake-gcs-server` (Testcontainers, a fake of the JSON API, not Google's service): put, get, conflict and already-exists, missing object (404), delete, then the real host with `Storage:Type=GoogleCloud` (push, download, duplicate, delete). The client is pointed at the container through the `STORAGE_EMULATOR_HOST` variable that the Google client honours; the production code has no test seam. | Same step as PostgreSQL in the CI `postgresql` job; same skip and `ELPAQUETO_REQUIRE_DOCKER` rules. |
| Container | `eng/docker/smoke-test.sh` | The built image as documented: non-root, read-only root filesystem, health, anonymous rules, `admin` commands, a real `dotnet nuget push` and `dotnet restore` with keys and passwords, revocation, graceful stop and restart. | `Docker` workflow (amd64 and arm64), or locally |
| Repository | `eng/hygiene/run_all.py`, `eng/format-strict.sh`, `eng/tests/port-upstream.test.sh` | Commit and content hygiene, secret scanning, strict formatting of ElPaqueto projects, the upstream port script. | CI `hygiene` and `build-test` jobs |

## Running

```bash
dotnet build ElPaqueto.sln
dotnet test ElPaqueto.sln                                              # everything, end-to-end tests included
dotnet test ElPaqueto.sln --filter "Category!=EndToEnd"                # what the build-test CI job runs
dotnet test tests/ElPaqueto.Tests --filter "Category=PostgreSql"          # needs Docker, skipped without it
dotnet test tests/ElPaqueto.Tests --filter "Category=Gcs"                 # needs Docker, skipped without it
dotnet test tests/ElPaqueto.Security.Tests                             # one project
dotnet test ElPaqueto.sln --filter "FullyQualifiedName~Authorization"  # by name
bash eng/format-strict.sh                                              # --fix applies the formatting
python3 eng/hygiene/run_all.py --range origin/develop..HEAD
bash eng/tests/port-upstream.test.sh
```

The container smoke test needs Docker and an image:

```bash
docker build -t elpaqueto:local .
bash eng/docker/smoke-test.sh elpaqueto:local
```

It publishes a port on `127.0.0.1` (18080 by default, `SMOKE_PORT`), creates random passwords and keys that never appear on a command line, and removes its container and volume at the end.

## Writing tests

- **Abuse cases, not only the happy path.** A security test covers invalid, expired, revoked, disabled and tampered input, and checks that the refusal is the expected one (same status, same headers).
- **A test must be able to fail.** When a test asserts that something does not happen, break the code on purpose once and check that it fails. Several tests carry a control case for this (for example `WebUiIntegrationTests.StatisticsPage_ListsTheConfiguredServices_OnlyWhenAskedTo`).
- **Real behaviour over mocks** for protocol and process behaviour: the integration host runs the real pipeline, process tests start the real binary, and the smoke test uses the real `dotnet` CLI.
- **Never log or print a secret**, even in tests: generate throwaway values, and pass them through standard input or the environment rather than arguments.
- **Each integration host has its own databases** in a temporary folder; test classes do not share state and run in parallel (the security suites share one host between the cases of a class, which run one after the other). The SQLite provider runs its asynchronous calls synchronously: use `Task.Run` to make concurrent writers really concurrent.
- **New route**: add a row to the permission table and a sample request, see [adding a protected endpoint](adding-a-protected-endpoint.md) and [security suites](#security-suites).
- **Inherited tests** are adapted, never skipped: see [upstream test changes](upstream-test-changes.md). The only `Skip` in the tree is the one `UnixFact` and `UnprivilegedUnixFact` set on Windows (or as root) for tests of Unix file modes and signals, which cannot run there; those tests run on the Linux leg of the CI.
- **New `Security` setting**: document it in [`docs/user/configuration.md`](../user/configuration.md) with its full key; `ConfigurationDocumentationTests` fails otherwise.

## Security suites

These suites in `tests/ElPaqueto.Tests` check the permission table (`EndpointPermissionTable`) against the endpoints the server really builds, and against the requests an attacker can send. They take their list of rows from the table itself; `Support/EndpointSamples.cs` only gives one concrete request per row.

| Suite | Checks |
| --- | --- |
| Endpoint inventory (`AuthorizationIntegrationTests`) | Every endpoint that can match a request has a row, and every row has an endpoint (`EveryMatchableEndpoint_HasARowInThePermissionTable`, `EveryRowOfThePermissionTable_MatchesARealEndpoint`, `EveryRouteNameOfTheWebProject_HasARowAndNoRowIsInvented`). An endpoint added without a row is denied and logged (`AnEndpointWithNoRow_IsDeniedAndLogged`, which builds a host with an extra route). |
| Authorization matrix (`AuthorizationMatrixIntegrationTests`) | Every row with every kind of credential: none, wrong password, unknown user, disabled user, users holding no permission, `Read`, `Publish`, `Admin` or all three, API keys with each permission in `X-NuGet-ApiKey`, revoked, expired, wrong-secret keys and keys of a disabled user, keys sent as a Basic password. The expected answer is computed from the row (401, 403, or allowed). A refusal must carry the right challenge, run no MVC action, Razor page or health check, and leave the packages (their versions and listing) and the list and sizes of the files in package storage unchanged; an allowed request must reach the code of the row. Each case starts from the same state: the test package published and listed. A second host checks the delete row when packages are deleted for good (Admin needed). |
| Request tampering (`RequestTamperingIntegrationTests`, `PathBaseTamperingIntegrationTests`) | Sent byte for byte through a real Kestrel listener, so that no client normalizes them: about thirty spellings of protected paths (case, trailing slash, `//`, `.` and `..` segments, `%2F`, `%2e`, double encoding, `;` parameters, backslashes, suffixes, absolute-form targets, the `/Index` alias, path base variants), every method a row does not allow (including `OPTIONS`, `TRACE`, WebDAV and unknown methods), and the `X-HTTP-Method-Override`, `X-HTTP-Method` and `X-Method-Override` headers. Each must get the refusal of the authorization layer itself, 401 or 403 as for the canonical path (a 404 would mean authorization let it through, since deny-by-default also covers URLs that match no endpoint), run no action, page or health check, and change no state. Only a NUL byte or an absolute-form target naming another host may be refused by Kestrel with 400 instead. Control tests check that the canonical paths are refused for the same credentials and that the variants still work with every permission. |

They rely on `Support/RequestProbe.cs`, test instrumentation that adds an `X-Test-Endpoint` response header naming the row of the endpoint the router chose, and records every MVC action, Razor page and health check that runs. Each suite shares one host per test class (`SecuritySuiteHost`, a class fixture) to keep its run time short: about 1,300 cases in under two minutes.

### When the inventory or the matrix fails after an upstream port

A port that adds or renames an endpoint fails these suites. Follow [adding a protected endpoint](adding-a-protected-endpoint.md): add a row with the least permission the endpoint needs and the reason in the pull request, then a sample request. Never fix the failure with an anonymous row, a broader permission than the endpoint needs, or by removing or loosening a test: an anonymous endpoint needs its own security review.

## What the tests do not cover

The NuGet clients other than the `dotnet` CLI and the client libraries, IDEs, debuggers fetching symbols, TLS (except a manual check through the proxy samples), the database and storage providers other than SQLite, PostgreSQL, the file system and Google Cloud Storage (against a fake server only), and deployments with several instances are not tested. See [client compatibility](../user/client-compatibility.md) and the [known limitations](../user/known-limitations.md).
