# Testing

## Levels

| Level | Where | What it covers | Runs in |
| --- | --- | --- | --- |
| Unit | `tests/ElPaqueto.Security.Tests` | The security module: options validation, password hashing, API key format and service, throttling, credential parsing, CLI commands, audit catalogue, module boundary, documentation coverage of the `Security` settings. | `dotnet test`, CI |
| Process | `tests/ElPaqueto.Security.Tests` (`*ProcessTests`, `HostProcess`) | The real server or `admin` command as a separate process: exit codes, startup failures, configuration loading, standard output and error. Some are Unix-only (`UnixFact`), for file modes and signals. | `dotnet test`, CI on Linux and Windows |
| Inherited unit | `tests/ElPaqueto.Core.Tests`, `tests/ElPaqueto.Protocol.Tests`, `tests/ElPaqueto.Web.Tests` | Inherited BaGetter tests, kept, plus the safe Markdown rendering of package READMEs. | `dotnet test`, CI |
| Integration | `tests/ElPaqueto.Tests` | The whole pipeline in-process (`ElPaquetoApplication`, a `WebApplicationFactory` with its own security database): authentication, the authorization matrix and endpoint inventory, API keys, throttling, transport and proxies, CORS, headers, audit logging, the web UI and its branding, the NuGet client libraries against the host, BaGetter upgrade checks. | `dotnet test`, CI |
| Container | `eng/docker/smoke-test.sh` | The built image as documented: non-root, read-only root filesystem, health, anonymous rules, `admin` commands, a real `dotnet nuget push` and `dotnet restore` with keys and passwords, revocation, graceful stop and restart. | `Docker` workflow (amd64 and arm64), or locally |
| Repository | `eng/hygiene/run_all.py`, `eng/format-strict.sh`, `eng/tests/port-upstream.test.sh` | Commit and content hygiene, secret scanning, strict formatting of ElPaqueto projects, the upstream port script. | CI `hygiene` and `build-test` jobs |

## Running

```bash
dotnet build ElPaqueto.sln
dotnet test ElPaqueto.sln                                              # everything
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
- **Each integration host has its own databases** in a temporary folder; tests do not share state and can run in parallel. The SQLite provider runs its asynchronous calls synchronously: use `Task.Run` to make concurrent writers really concurrent.
- **New route**: add it to the authorization matrix, see [adding a protected endpoint](adding-a-protected-endpoint.md).
- **New `Security` setting**: document it in [`docs/user/configuration.md`](../user/configuration.md) with its full key; `ConfigurationDocumentationTests` fails otherwise.

## What the tests do not cover

The NuGet clients other than the `dotnet` CLI and the client libraries, IDEs, debuggers fetching symbols, the non-default database and storage providers, and deployments with several instances are not tested: see [known limitations](../user/known-limitations.md).
