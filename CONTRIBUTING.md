# Contributing to ElPaqueto

Thank you for helping. This guide explains how to build the project, how to propose a change, and the rules a pull request must follow to be merged.

ElPaqueto is a fork of BaGetter. Some of the rules below exist so that upstream changes can keep being ported into this repository.

## Prerequisites

- The **.NET 10 SDK**. It includes the runtime. The exact SDK version is pinned in `global.json`. Run `dotnet --list-sdks` to see what is installed.
- [Node.js], only if you work on the documentation site.
- Docker, only if you build or test the container image.

If `dotnet` reports `A compatible .NET SDK was not found`, the installed SDK does not satisfy `global.json`: install the .NET 10 SDK from the [.NET download page][.NET SDK]. Do not edit `global.json` to work around it: local builds, CI and the container image must use the same SDK.

## Run from source

1. Clone the repository
2. Navigate to `src/ElPaqueto`
3. Start the service with `dotnet run`
4. Open the URL `http://localhost:5000/v3/index.json` in your browser

## Repository layout

- `src/` holds the server. Most projects are inherited from BaGetter and were renamed to `ElPaqueto.*` (`ElPaqueto.Core`, `ElPaqueto.Protocol`, `ElPaqueto.Web`, the database and storage providers and the host `ElPaqueto`). Upstream changes to them are ported, see [Keeping upstream ports easy](#keeping-upstream-ports-easy).
- `src/ElPaqueto.Security` is new to ElPaqueto: authentication, authorization and API keys. New features go into new projects like this one.
- `tests/` mirrors the project names: `ElPaqueto.Core.Tests` tests `ElPaqueto.Core`, `ElPaqueto.Security.Tests` tests `ElPaqueto.Security`.

## Build and test

Every pull request and every push to `develop` or `main` runs the `CI` workflow on GitHub-hosted Ubuntu and Windows runners. It restores, builds in Release mode and runs the whole test suite, then publishes the TRX results as an artifact and fails when no test was executed. It needs no secret, so it also runs for pull requests from forks.

Run the same commands locally before you open a pull request:

```bash
dotnet restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build --filter "Category!=EndToEnd"
```

A failing test must be fixed or explained in the pull request. Do not skip or disable it to make a check pass.

### End-to-end tests

`tests/ElPaqueto.EndToEnd.Tests` starts the published server as a child process and drives it with the real `dotnet` CLI (`dotnet nuget push`, `dotnet restore`, `dotnet build`, `dotnet test`, ...) in isolated NuGet folders. These tests are slow, so they carry `[Trait("Category", "EndToEnd")]`: the `build-test` job of the `CI` workflow excludes them with `--filter "Category!=EndToEnd"`, and the `e2e` job runs them on Ubuntu and Windows. Run them locally after a build, in the same configuration:

```bash
dotnet build --configuration Release
dotnet test tests/ElPaqueto.EndToEnd.Tests --configuration Release --no-build
```

A run takes several minutes and needs Internet access for one test (it restores `xunit` from nuget.org). The tests never touch your own NuGet configuration, caches or the repository tree: everything is written below a temporary directory that is deleted at the end. The server is reached over plain HTTP on `127.0.0.1`, which the server allows for loopback and NuGet accepts because the harness adds the source with `--allow-insecure-connections`; TLS is not tested there. Running `dotnet test` on the whole solution also runs them; add `--filter "Category!=EndToEnd"` to skip them. See [End-to-end tests](docs/technical/e2e-tests.md).

## Container image

The `Docker` workflow (`.github/workflows/docker.yml`) runs on every pull request and every push to `develop` or `main`. It pushes nothing to any registry and needs no secret.

- `image (amd64)` and `image (arm64)` build the image natively on each architecture, check its metadata (user, port, volume, health check, labels) and run the smoke test.
- `cross-build` builds the `linux/arm64` image from an amd64 runner, the way a multi-platform build does.
- `scan` scans the amd64 image with Trivy and fails on HIGH or CRITICAL findings that have a fixed version. On pushes, `scan-report` uploads the same scan to code scanning.
- `samples` validates `docker-compose.example.yml` and the reverse-proxy samples.

Reproduce it locally (Docker, Python 3 and `openssl` needed; on Windows, use Git Bash):

```bash
docker build -t elpaqueto:local .
bash eng/docker/smoke-test.sh elpaqueto:local          # runtime checks, see the script header
bash eng/docker/validate-proxy-samples.sh               # Compose and proxy samples

docker save elpaqueto:local -o elpaqueto.tar
bash eng/docker/scan-image.sh --prepare                 # pinned Trivy (SHA-256 checked) and its database
bash eng/docker/scan-image.sh elpaqueto.tar
```

The smoke test checks the anonymous behaviour (health, `401` with the Basic challenge on `/v3/index.json` and `/health/details`, `403` for remote plain HTTP), the non-root user, the read-only root filesystem and the owner-only security database. It then creates an administrator with `admin bootstrap --password-stdin`, a reader and a publisher, checks their permissions, pushes and restores a package with the real `dotnet` CLI, stops the container (graceful exit within 10 s) and restores again after the restart. It needs no secret: the passwords are random and masked in the job log. Pushing with an API key will be added once API-key authentication is merged.

**Accepted scan findings** go in `.trivyignore`, one entry per finding, each directly below a comment that justifies it, with an expiry date at most a year ahead:

```text
# The vulnerable code path (XML signature parsing) is never reached; no fixed package yet.
CVE-2026-12345 exp:2027-03-31
```

An entry is accepted in review only when the finding cannot be fixed by updating the base image or a package, and when the justification explains why the server is not exposed. After the expiry date, Trivy reports the finding again and the hygiene check fails: review the entry again, then remove or extend it. `eng/hygiene/check_trivyignore.py` enforces the format. Trivy's version and checksums are pinned in `eng/hygiene/install_tools.py`.

## Code quality

Code written for ElPaqueto builds under a strict baseline: nullable reference types, warnings as errors and a pinned analysis level (`eng/ElPaqueto.Build.props`). The baseline applies only to the projects listed in `ElPaquetoStrictProjects` in that file. Add a new project to the list in the pull request that creates it.

- Code inherited from BaGetter keeps its historical settings. Warnings in inherited code are not fixed here, so that upstream changes stay mechanical to port. Fix them upstream and port the fix back.
- ElPaqueto code must build without warnings. Every suppression needs a justification, either `[SuppressMessage(Justification = "...")]` or a comment next to the `.editorconfig` entry.
- Formatting of the strict projects is verified in CI. Run the same check locally, or apply the formatting:

```bash
bash eng/format-strict.sh          # verify
bash eng/format-strict.sh --fix    # apply
```

Files authored for ElPaqueto use LF line endings on every platform (see `.gitattributes`).

## Proposing a change

1. **Start with an issue.** For a bug, use the bug report form. For a new feature or a behaviour change, open a feature request first and wait for feedback before writing code: it avoids work that cannot be merged. Small fixes such as typos can go straight to a pull request.
2. **Fork the repository** and create a branch from `develop`.
3. **Make focused changes.** One pull request addresses one issue. Unrelated fixes go in separate pull requests.
4. **Open a pull request against `develop`.** The pull-request template is filled in automatically.

### Branching

- `develop` is the integration branch. All pull requests target `develop`.
- `main` receives releases only. Do not open pull requests against `main`.
- Name branches after their purpose and issue, for example `fix/123-search-paging` or `feat/45-api-key-revocation`.

### Commits

Write short English commit messages that follow [Conventional Commits]:

```text
<type>(<optional scope>): <summary in the imperative mood>
```

- Common types: `feat`, `fix`, `docs`, `test`, `refactor`, `build`, `ci`, `chore`.
- Keep the summary line short (about 72 characters at most), lower case, without a trailing period.
- Use the body, after a blank line, to explain why the change is needed when the summary is not enough.
- Keep commits atomic: each commit builds and contains one logical change.

Examples:

```text
feat(auth): add API key revocation
fix(docker): create data directories with the right owner
docs: document reverse proxy configuration
```

### Pull requests

The pull-request template contains two sections:

- **`## Summary`**: what changes and why, and the issue it closes (`Closes #123`).
- **`## Validation`**: the commands you actually ran and their real results, for example the test counts reported by `dotnet test`, or the NuGet client commands used against a running server. List anything you could not verify. Do not claim checks you did not run.

Complete the checklist in the template. A maintainer reviews the pull request once the `CI` workflow passes.

## Definition of done

A change is ready to merge when:

- automated tests cover the change, and the whole suite passes;
- changes that affect the NuGet protocol (service index, search, package content, push, delete, authentication) are validated with a real NuGet client such as `dotnet nuget push`, `dotnet restore` or `nuget.exe`, not only with mocks. NuGet clients refuse plain-HTTP sources unless `allowInsecureConnections` is set for that source: say in the Validation section whether you used HTTPS or that setting;
- documentation (`docs/`, `readme.md`, this guide) is updated in the same pull request;
- no test is skipped or disabled.

## Keeping upstream ports easy

Most of the code comes from BaGetter. Upstream fixes are ported into ElPaqueto, and every edit to an inherited file can turn a mechanical port into a manual one. Follow these rules:

- **Put new features in new projects**, for example a dedicated `ElPaqueto.Security` project, not in files inherited from upstream.
- **Keep edits to inherited files small**, ideally a one-line hook that calls into new code.
- **Avoid gratuitous refactoring, reformatting, file moves and renames** of inherited files, even when they would improve the code.
- **List every inherited file you changed** in the pull request, with the impact on upstream porting.

The background for these rules is tracked in the project identity and rebranding scope decision ([#5]), the authentication architecture decision ([#6]) and the upstream synchronization process ([#13]), which will add `docs/upstream-porting.md`.

## Security

- **Do not report vulnerabilities in public issues or pull requests.** Follow the [security policy](SECURITY.md) to report them privately.
- **Never post secrets**: passwords, API keys, `Authorization` headers, connection strings, certificates or tokens. Use placeholders such as `<api-key>`.
- **Redact logs and configuration** before you attach them to an issue or a pull request.
- **Never commit secrets**, even in tests or samples. Use obvious placeholder values.

## Code of conduct

ElPaqueto has no code of conduct yet: one will be adopted once the project has its own reporting channel. Until then, be respectful and constructive in issues, pull requests and reviews.

## Releases

No release automation exists yet: the inherited upstream workflows that published packages, Docker images and documentation were removed. Nothing is published automatically from this repository until a dedicated release workflow is added.

[.NET SDK]: https://www.microsoft.com/net/download
[Node.js]: https://nodejs.org/
[Conventional Commits]: https://www.conventionalcommits.org/
[#5]: https://github.com/sputier/ElPaqueto/issues/5
[#6]: https://github.com/sputier/ElPaqueto/issues/6
[#13]: https://github.com/sputier/ElPaqueto/issues/13
