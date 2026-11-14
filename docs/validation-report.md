# Validation report

What has been verified about ElPaqueto, how, and what has not. Every row points to its evidence: a test that runs in CI, a local run made for this report, or a dated manual record in a merged pull request. A row without evidence is **Not verified**. The [known limitations](user/known-limitations.md) give the same gaps from a user's point of view.

| | |
| --- | --- |
| ElPaqueto version | `0.1.0-prerelease` (no release yet) |
| Commit validated | `54101e5` on `develop` (pull request #107 merged); the pull request that adds this report changes documentation only |
| Upstream baseline | BaGetter `02b6706` (2026-04-08), see [porting upstream changes](upstream-porting.md) |
| Report date | 2026-11-14 |

## Evidence sources

| Id | Source | Toolchain |
| --- | --- | --- |
| **L** | Local run on 2026-11-14 of the commit above: `dotnet build -c Release`, fast suite, end-to-end category, `eng/format-strict.sh`, `eng/hygiene/run_all.py`. | Windows 11 Pro 10.0.26200, .NET SDK 10.0.404 (runtime 10.0.15), `dotnet nuget --version` = `7.9.0-rc.3019+2ab2854867733112c84ac12c4273d7970ac3afdf`, Python 3.14.10, Git 2.56.0. No Docker on this machine. |
| **CI** | [CI run 43197768521](https://github.com/sputier/ElPaqueto/actions/runs/43197768521) on the push of `54101e5` to `develop` (2026-11-12): `hygiene`, `build-test` and `e2e` on both OS, all green. | `ubuntu-latest` = image `ubuntu-26.04` (20261101.186.1); `windows-latest` = image `windows-2025-vs2026` (20261103.298.1); .NET SDK 10.0.404 and runtime 10.0.15, installed by `actions/setup-dotnet` from `global.json` (10.0.401, `latestFeature`). |
| **D** | [Docker run 43197768702](https://github.com/sputier/ElPaqueto/actions/runs/43197768702) on the same push: `image (amd64)`, `image (arm64)`, `cross-build`, `scan`, `samples`, all green. | `ubuntu-24.04` (amd64) and `ubuntu-24.04-arm` (arm64) runners; base images `sdk:10.0.404-alpine3.24` and `aspnet:10.0.15-alpine3.24`; Trivy 0.76.1. |
| **M** | Manual run recorded in the Validation section of a merged pull request, by the change author, on the date given. Not repeated for this report. | As recorded in the pull request. |

### Test counts

| Suite | L (Windows 11) | CI ubuntu-26.04 | CI windows-2025 |
| --- | --- | --- | --- |
| Fast suite (`--filter "Category!=EndToEnd"`) | 1615 total, 1611 passed, 4 skipped, 0 failed | 1615 run, 1615 passed | 1615 total, 1611 passed, 4 skipped |
| End-to-end (`--filter "Category=EndToEnd"`) | 65 passed, 0 failed, in each of two consecutive runs (about 3 min each) | 65 passed | 65 passed |
| Hygiene (`eng/hygiene/run_all.py`) | all checks passed: 51 self-tests, actionlint 1.7.12, gitleaks 8.30.1, vulnerable packages | job `hygiene` green | |
| Strict formatting (`eng/format-strict.sh`) | exit 0 | step of `build-test` green | step of `build-test` green |

Per project in the fast suite (both CI legs): Core 187, Protocol 59, Protocol.Samples 8, Web 82, ElPaqueto.Tests 420, Security 859. The 4 tests skipped on Windows are Unix-only by design (file modes, unreadable files): `AuthDatabaseFileTests.Prepare_OnUnix_RestrictsTheDirectoryAndTheFileToTheOwner`, `AuthDatabaseFileTests.RestrictToOwner_NarrowsAFileThatOtherUsersCouldRead`, `StartupConfigurationProcessTests.UnreadableConfigurationFile_ExitsWithCode1AndAMessage` and `StartupConfigurationProcessTests.UnreadableSecretFile_FailsTheBuildWithAnExceptionThatMainReportsAsAConfigurationFailure`. They run and pass on Linux.

### Status values

- **Verified**: an automated check that passed both in the local run for this report (L) and in CI.
- **Verified in CI only**: an automated check that passes in CI on every build but could not be re-run locally for this report (the container checks: no Docker locally), or that ran in CI on an OS not available locally.
- **Partially**: part of the criterion has evidence; the Notes column says which part has none.
- **Not verified**: no evidence. Not a claim that it fails.
- **N/A**: does not apply to this version.

Test names below are `Class.Method`. End-to-end tests (`E2E`) are in `tests/ElPaqueto.EndToEnd.Tests`, integration tests in `tests/ElPaqueto.Tests`, unit and process tests in `tests/ElPaqueto.Security.Tests`, the container checks in `eng/docker/smoke-test.sh`.

## 1. Acceptance matrix

### Real NuGet client scenarios

All `E2E` rows run the published server as a child process and the real `dotnet` CLI with isolated NuGet folders over plain HTTP on loopback ([end-to-end tests](technical/e2e-tests.md)). TLS is not part of them.

| # | Criterion | Evidence | Client, OS | Status | Notes |
| --- | --- | --- | --- | --- | --- |
| C1 | Add the source with credentials, list it | E2E `SourceAndPublishTests.Scenario01_AddSource_WithCredentialsAndInsecureConnectionsAllowed_Succeeds`, `Scenario02_ListSource_ShowsTheSourceEnabled_AndNeverThePassword`, `Scenario03_CredentialsFromTheEnvironment_AuthenticateASearch` | `dotnet`, L + CI both OS | Verified | Stored with `--store-password-in-clear-text` in the throwaway test config only. The Windows DPAPI path (`Password` instead of `ClearTextPassword`) is not tested. |
| C2 | `dotnet pack` then `dotnet nuget push` with a Publish key; package present, metadata right, downloadable | E2E `SourceAndPublishTests.Scenario04_Push_WithAPublishKey_PublishesThePackage`, `HarnessTests.Smoke_PushWithAPublishKey_ThenRestoreWithClearedCaches`; smoke test "dotnet nuget push --api-key (Publish key) and dotnet restore (reader)" | `dotnet`, L + CI both OS; D (container) | Verified | |
| C3 | Symbol package push | E2E `SourceAndPublishTests.Scenario05_Push_WithASymbolPackage_PublishesTheSymbolsThroughSymbolPackagePublish` | `dotnet`, L + CI | Verified | Symbol download by a debugger is not verified. |
| C4 | Push refused with an invalid, revoked or expired key, a read-only key, an owner without Publish, a disabled owner, no key; nothing stored | E2E `SourceAndPublishTests.Scenario06_Push_IsRefused_AndChangesNothing` (7 cases), `Scenario07_Push_WithBasicCredentialsHoldingPublish_AndNoApiKey_IsRefusedWith403` | `dotnet`, L + CI | Verified | Invalid, revoked, expired key and disabled owner: 401. Read-only key, owner without Publish, no key: 403. |
| C5 | Duplicate version | E2E `DuplicateAndVersioningTests.Scenario08_PushOfAnExistingVersion_IsRefusedWith409_AndKeepsTheStoredPackage`, `Scenario09_PushWithSkipDuplicate_ReportsTheConflictAsSkipped` | `dotnet`, L + CI | Verified | 409; stored bytes unchanged. |
| C6 | `dotnet add package` | E2E `ConsumeTests.Step1_AddPackage_WritesThePackageReference`, `DuplicateAndVersioningTests.Scenario11`–`Scenario13` | `dotnet`, L + CI | Verified | |
| C7 | Restore after `dotnet nuget locals all --clear`, isolated `NUGET_PACKAGES`, transitive dependency, non-interactive | E2E `ConsumeTests.Step2_Restore_WithClearedCaches_ResolvesTheTransitiveDependencyFromTheServer`, `Step3_ASecondRestoreAfterAnotherClear_DownloadsFromTheServerAgain`, `Step6_Restore_WithoutCredentials_FailsWith401_WithoutPrompting` | `dotnet`, L + CI | Verified | Download counts prove the second restore reached the server. |
| C8 | `dotnet build` | E2E `ConsumeTests.Step4_Build_OfCodeThatUsesAPackageType_Succeeds` | `dotnet`, L + CI | Verified | |
| C9 | `dotnet test` | E2E `ConsumeTests.Step5_Test_OfAnXunitProjectUsingThePackage_PassesWithSourceMapping` | `dotnet`, L + CI | Verified | Needs nuget.org for xunit; package source mapping keeps ElPaqueto packages on ElPaqueto. |
| C10 | V3 search, not reachable anonymously | E2E `DuplicateAndVersioningTests.Scenario10_Search_ListsStableVersions_AndThePrereleaseOnlyOnRequest`, `ServiceIndexCrawlTests` (search URLs) | `dotnet package search`, L + CI | Verified | `dotnet package search` exits 0 when the source refuses it (see section 3). Search in Visual Studio: Not verified (#43). |
| C11 | Versions 1.0.0, 1.1.0, 1.2.0-preview.1: discovery, exact install, stable and prerelease restore, SemVer ranges | E2E `DuplicateAndVersioningTests.Scenario10`–`Scenario14` (`1.*` → 1.1.0, `1.*-*` → 1.2.0-preview.1) | `dotnet`, L + CI | Verified | |
| C12 | Unlist and relist | E2E `UnlistTests.Scenario1`–`Scenario6` | `dotnet nuget delete`, raw `POST` for relist; L + CI | Verified | No `dotnet` command relists. An unlisted version is still chosen by a version range (section 3). |
| C13 | Hard delete, and its refusal without `Admin` | E2E `HardDeleteTests.Scenario7_HardDeleteWithAnAdminKey_RemovesTheVersion_AndItsRestoreFails`, `Scenario8_HardDeleteWithAPublishOnlyKey_IsRefusedWith403_AndKeepsTheFiles` | `dotnet nuget delete`, L + CI | Verified | The inherited file storage leaves the empty version folder behind (section 3). |
| C14 | Restart persistence, graceful and abrupt | E2E `PersistenceAfterStopTests.Scenario09To11_EverythingSurvivesAStopAndARestart`, `PersistenceAfterKillTests.Scenario12_EverythingSurvivesAKillAndARestart`; smoke test "the security database file survived the restart", "dotnet restore after the restart" | `dotnet`, L + CI; D | Verified | The graceful stop (SIGTERM) runs on Linux only; on Windows the "stop" test kills the server and says so. |

### Authorization matrix (specification, 10 rows)

| Row | Criterion | Evidence | Status |
| --- | --- | --- | --- |
| 1 | No credentials → 401 | E2E `AuthorizationMatrixTests.Row01_NoCredentials_RestoreFailsWith401WithoutPrompting_AndTheIndexChallenges`; integration `AuthorizationIntegrationTests.Anonymous_IsAnsweredWith401OnEveryRoute`; smoke test | Verified |
| 2 | Wrong password → 401 | E2E `Row02_WrongPassword_RestoreFailsWith401WithoutPrompting` | Verified |
| 3 | Disabled user → 401 | E2E `Row03_DisabledUser_RestoreFailsWith401WithoutPrompting` | Verified |
| 4 | Authorized user → OK | E2E `Row04_ReadUser_RestoreSucceeds` | Verified |
| 5 | No `Read` → 403 | E2E `Row05_UserWithoutRead_IsRefusedWith403_BySearchAndRestore` | Verified |
| 6 | Publish without authentication → refused | E2E `Row06_PushWithoutAKey_IsRefused_AndThePackageIsAbsent` | Verified |
| 7 | Valid key → push OK | E2E `Row07_PushWithAPublishKey_Succeeds` | Verified |
| 8 | Revoked key → refused | E2E `Row08_PushWithARevokedKey_IsRefused_AndThePackageIsAbsent` | Verified |
| 9 | Non-administrator on administration → 403 | E2E `Row09_NonAdministrator_IsRefusedWith403_OnEveryAdminOperation_WithoutSideEffect` | Verified |
| 10 | Direct `.nupkg` without authentication → 401 | E2E `Row10_PackageDownloadWithoutRead_ReturnsNoBytes_AndCountsNoDownload` | Verified |

All rows: real `dotnet` CLI or raw HTTP against the real server, L + CI on both OS. Administration itself happens through the `admin` commands, which have no HTTP surface; row 9 covers the HTTP operations that need `Admin` (hard delete, `/health/details`).

### Protocol surface and transport

| # | Criterion | Evidence | Status | Notes |
| --- | --- | --- | --- | --- |
| P1 | Every URL reachable from the V3 index is protected; no alternative endpoint exposes packages | E2E `ServiceIndexCrawlTests` (6 tests, 22 URLs crawled in the recorded run); integration `AuthorizationIntegrationTests.EveryMatchableEndpoint_HasARowInThePermissionTable`, `EveryRowOfThePermissionTable_MatchesARealEndpoint` | Verified | |
| P2 | Generated absolute URLs cannot be redirected with forwarded headers | E2E `ServiceIndexCrawlTests.ForwardedHeadersFromAnUntrustedClient_NeverReachTheGeneratedUrls`; integration `ProxyAndTransportIntegrationTests.PublicBaseUrl_PinsEveryGeneratedUrl`, `UntrustedRemote_SpoofedProtoAndHost_IsTreatedAsPlainHttpAndRejected` | Verified | |
| P3 | `401` carries `WWW-Authenticate: Basic realm="ElPaqueto"`, no redirect; `403` for authenticated callers | integration `BasicAuthenticationIntegrationTests`, `AuthorizationIntegrationTests`; E2E crawl (empty bodies, challenge); smoke test | Verified | |
| P4 | HTTPS required for remote clients; proxy headers trusted only from listed proxies | integration `ProxyAndTransportIntegrationTests` (29 tests); smoke test "remote plain HTTP is refused with 403 and no challenge" | Verified (in-process), Verified in CI only (container) | TLS itself is never terminated by ElPaqueto. |
| P5 | Works behind nginx, Caddy and Traefik with TLS | D `samples` job: `docker compose config`, `nginx -t`, `caddy validate` on the shipped samples. M (#91, 2026-11-12): push and restore through each proxy with a throwaway CA, nginx 1.30.6, Caddy 2.11.9, Traefik 3.7.18 | Partially | The automated check is syntax only. TLS end to end was run once by hand; tunnels (for example Cloudflare Tunnel) were never run. |

### Security properties

| # | Criterion | Evidence | Status | Notes |
| --- | --- | --- | --- | --- |
| S1 | Passwords hashed with a proven algorithm, salt and cost | `PasswordServiceTests` (V3 format, PBKDF2-HMAC-SHA512, 210 000 iterations, 128-bit salt; committed reference hashes) | Verified | |
| S2 | Brute-force throttling | `LoginThrottleTests`, `ThrottlingIntegrationTests`, `ThrottlingProcessTests`, `ApiKeyThrottlingIntegrationTests`; E2E `ThrottledClientTests` (2 tests) | Verified | In memory, per process (section 4). |
| S3 | User-enumeration and timing resistance | `BasicAuthenticationIntegrationTests.WrongPasswordUnknownUserAndDisabledUser_GetByteIdenticalResponses`, `PasswordServiceTests.Verify_ForAKnownUser_PerformsTheSameNumberOfVerificationsAsForAnUnknownOne`, `ThrottlingIntegrationTests.KnownAndUnknownUserNames_GetIdenticalAnswersAtEveryStep`, `ApiKeyServiceTests.Validate_AnUnknownKeyId_Fails_AfterTheSameComparisonWork` | Verified | Same work is asserted by call counts, not by measuring time. |
| S4 | API keys: individual, scoped, expiring, revocable, rotatable, hashed, shown once | `ApiKeyServiceTests`, `ApiKeyCommandTests`, `ApiKeyCommandProcessTests`, `ApiKeyAuthenticationIntegrationTests` (40 tests); E2E C4 | Verified | |
| S5 | No secret in logs, errors or test output | `AuditLoggingIntegrationTests.EveryDecision_IsAudited_AndNoLogEntryContainsACredential`, `AdminCommandAuditTests`, `AuditLoggingProcessTests`, `LoggingGuardTests`, `BootstrapCommandTests.ThePasswordAndItsHash_NeverAppearInAnyOutputOrLog`; E2E `HarnessTests.ASecretFedThroughTheRunner_IsRedactedFromEverythingLogged` | Verified | M (#101): the Ubuntu E2E TRX artifact was scanned for the generated secrets once; this scan is not automated. |
| S6 | Read-only principals can never write; `Admin` implies nothing else | `AuthorizationIntegrationTests.PublishPermission_DoesNotImplyRead`, `AdminPermission_ImpliesNeitherReadNorPublish`, `APasswordWithPublish_NeverOpensAWriteRoute`; E2E C4, matrix rows 5–9 | Verified | |
| S7 | Web UI: no script from package metadata, CORS closed, security headers | `PackageMarkdownFacts`, `WebUiIntegrationTests`, `CorsIntegrationTests`, `SecurityHeadersIntegrationTests` | Verified | The content security policy keeps `'unsafe-inline'` and `'unsafe-eval'` (section 4). Real browser: M (#89 and #98, Chromium 145 headless, 0 CSP violations); Firefox and Safari not verified. |
| S8 | Legacy BaGetter `ApiKey`/`Authentication` settings cannot reopen the feed | `LegacyAuthenticationSettingsTests`, `StartupConfigurationProcessTests` | Verified | |
| S9 | Configuration errors stop the process without printing values | `StartupConfigurationProcessTests`, `SecurityConfigurationHostTests` | Verified | Unreadable-file cases on Linux only (CI). |

### Administration

| # | Criterion | Evidence | Status | Notes |
| --- | --- | --- | --- | --- |
| A1 | No default account; first administrator by a secure bootstrap | `BootstrapCommandTests` (including two concurrent bootstraps), `AdminCommandProcessTests`; smoke test "a second admin bootstrap is refused (exit code 3)" | Verified | The interactive no-echo prompt needs a TTY: tested through a fake only. |
| A2 | Create, disable, enable a user; change permissions; reset a password; effect on the next request | `UserCommandTests`, `AdminCommandAuthenticationIntegrationTests` (child process next to a running host) | Verified | |
| A3 | Create, list, revoke, rotate API keys | `ApiKeyCommandTests`, `ApiKeyCommandProcessTests`; smoke test "admin key create", "admin key revoke" | Verified | |

### Persistence and upgrades

| # | Criterion | Evidence | Status | Notes |
| --- | --- | --- | --- | --- |
| U1 | Model and migrations in sync; migrate from empty; migrate twice | `AuthDbContextTests.Model_HasNoPendingChanges`, `Migrate_FromEmpty_CreatesTheSchema`, `Migrate_Twice_AppliesNothingAndLosesNoRow` | Verified | |
| U2 | Upgrade of the security database with existing data | `AuthDbContextTests.Migrate_FromTheInitialSchemaWithUsers_AddsApiKeysAndKeepsTheUsers` | Partially | Covers the one existing upgrade. The step-by-step seed pattern, the CLI-then-server ordering, the 200-reader concurrency stress and the automated backup test of #42 do not exist yet. |
| U3 | Backup and restore | M (#98, 2026-11-12): the stopped-container `tar` procedure of [backup](user/backup.md), restored into a new volume, same packages and keys, owners and modes kept | Partially | Manual, once. The online (`VACUUM INTO`/`.backup`) variant is documented as untested. No automated test (#42). |
| U4 | Image upgrade N → N+1 on the same volume | none | N/A | No release image exists yet (#42, #46). |
| U5 | Upgrade from BaGetter | `BaGetterDatabaseUpgradeIntegrationTests`, `BaGetterUpgradeChecksTests`, `StartupConfigurationProcessTests` | Partially | The "BaGetter" database in the test is created by ElPaqueto under the name `bagetter.db`; no BaGetter binary was run. |

### Container image

| # | Criterion | Evidence | Status | Notes |
| --- | --- | --- | --- | --- |
| K1 | Builds for `linux/amd64` and `linux/arm64` | D `image (amd64)`, `image (arm64)` (native), `cross-build (arm64 from amd64)` | Verified in CI only | |
| K2 | Non-root (UID 1654), read-only root filesystem, no capabilities, `no-new-privileges` | smoke test "runs as UID 1654", "the root filesystem is read-only", "the application folder is not writable by the process", "healthy after 6s (read-only root, all capabilities dropped, no-new-privileges)" | Verified in CI only | `/tmp` must be a writable tmpfs: without it a push fails with 500 (M, #91). |
| K3 | Health check, graceful stop, restart on the same volume | smoke test "anonymous /health: 200", "docker stop: graceful exit (code 0)", "the logs show a graceful shutdown", restart steps | Verified in CI only | |
| K4 | Security database file mode 600, owned by the container user | smoke test "/data/db/auth.db exists, mode 600, owned by UID 1654" | Verified in CI only | On a bind mount, `/data/db` is created with the default mode (M, #91; documented in [Docker](user/docker.md)). |
| K5 | Image vulnerability scan | D `scan`: Trivy 0.76.1, `--severity HIGH,CRITICAL --ignore-unfixed`: 0 findings (Alpine 3.24.4, the application and runtime `deps.json` files) | Verified in CI only | Unfixed vulnerabilities and lower severities are not gating. |
| K6 | Compose example | D `samples`: `docker compose config -q`. M (#91, 2026-11-12): `up -d`, healthy, no published port, read-only, capabilities dropped, verbatim `admin bootstrap` from a Docker secret | Partially | `up` was run by hand once. |
| K7 | Configuration through environment variables and Docker secrets (`/run/secrets`) | key-per-file tests in `ElPaqueto.Security.Tests`; M (#91): Compose secret | Partially | A secret file the container user cannot read stops the server with exit code 1 (#93); the Swarm-less `uid/gid/mode` limitation is documented. |

### Repository, build and release

| # | Criterion | Evidence | Status | Notes |
| --- | --- | --- | --- | --- |
| R1 | Build with no error on both OS | L; CI `build-test` | Verified | |
| R2 | Strict formatting of ElPaqueto projects | L `eng/format-strict.sh`; CI | Verified | |
| R3 | Hygiene: secrets, commit authorship, infrastructure details, workflow rules, vulnerable packages | L and CI `eng/hygiene/run_all.py` (actionlint 1.7.12 and gitleaks 8.30.1 included) | Verified | The deliberate-violation run is M (#68). |
| R4 | BaGetter non-regression | Inherited suites Core 187, Protocol 59, Protocol.Samples 8 pass; Web and ElPaqueto.Tests also hold inherited tests | Partially | The adaptation of inherited integration tests to deny-by-default is tracked in #41 (open). |
| R5 | Release workflow publishes a multi-arch image with provenance and SBOM | `eng/tests/release.test.sh` (74 assertions, CI both OS); M (#97): local rehearsal against a local registry | Not verified | The real publish path (GHCR push, attestations, GitHub Release, `release` environment) has never run (#47). |

## 2. Platform and client matrix

| Client | Version | OS | Status | Evidence |
| --- | --- | --- | --- | --- |
| `dotnet` CLI | SDK 10.0.404, NuGet `7.9.0-rc.3019` | Windows 11 Pro 10.0.26200 (x64) | Verified, 2026-11-14 | L: end-to-end category |
| `dotnet` CLI | SDK 10.0.404 | Windows Server 2025 (`windows-2025-vs2026` runner) | Verified in CI only, 2026-11-12 | CI `e2e (windows-latest)` |
| `dotnet` CLI | SDK 10.0.404 | Ubuntu 26.04 (`ubuntu-26.04` runner) | Verified in CI only, 2026-11-12 | CI `e2e (ubuntu-latest)` |
| `dotnet` CLI in the .NET SDK container | .NET 10 SDK image | Linux amd64 and arm64 (Docker on `ubuntu-24.04`, `ubuntu-24.04-arm`) | Verified in CI only, 2026-11-12 | D smoke test: push and restore |
| `dotnet` CLI through nginx, Caddy, Traefik with TLS | .NET 10 SDK | Linux containers on Docker Desktop | Partially (manual, 2026-11-12) | M (#91) |
| NuGet client libraries | as referenced by the tests | Windows, Linux | Verified | integration tests in `tests/ElPaqueto.Tests` |
| `nuget.exe` | none | Windows | **Not verified** | #43 |
| `nuget.exe` on Mono | none | Linux, macOS | **Not verified** | #43 |
| Visual Studio | none | Windows | **Not verified** | #43 |
| JetBrains Rider, VS Code C# Dev Kit, MSBuild-only builds, Paket, PowerShellGet | none | any | **Not verified** | |
| Any client | none | macOS | **Not verified** | no macOS run |
| Web UI in a browser | Chromium 145 (Playwright, headless) | Windows | Partially (manual, 2026-11-12) | M (#89, #98) |
| Web UI in Firefox or Safari | none | any | **Not verified** | |
| Debugger fetching symbols | none | any | **Not verified** | |

The server itself was run on Windows 11 (L), Windows Server 2025 and Ubuntu 26.04 (CI, published binary) and in the Linux container (D). Windows service, systemd and IIS hosting are not tested.

## 3. What the real clients do

Observed with the `dotnet` CLI and asserted by the end-to-end tests, so a change in the client or the server makes a test fail. Details: [end-to-end tests](technical/e2e-tests.md#what-the-real-clients-do).

- **Every request is sent twice.** NuGet sends each request anonymously first, gets `401` with the challenge, then repeats it with the source credentials. It never sends Basic credentials pre-emptively.
- **401**: a restore with a wrong password sends 6 identical credentialed attempts within seconds, then fails with `NU1301` and `401 (Unauthorized)`, without a prompt. The server counts the six as one failed sign-in (#99, fixed in #107): two such restores still end with `401`, and the right password works right after (`ThrottledClientTests.ARestoreWithAStalePassword_FailsWith401_IsCountedOnce_AndTheRightPasswordWorksRightAfter`).
- **403**: fails with `403 (Forbidden)`; a push with Basic credentials and no `--api-key` warns `No API Key was provided`, then gets `403`.
- **429**: the client retries 6 times in about 8 seconds and **does not honour `Retry-After`** (60 s), then fails (`ThrottledClientTests.DistinctWrongPasswords_StillTripTheDefaultThrottle_AndTheClientThenReports429WithoutWaiting`).
- **503** (no free verification slot): 6 retries in about 6 seconds without waiting for `Retry-After`, then failure (`BusyServerClientTests.ARestoreWhileEveryVerificationSlotIsBusy_FailsWith503_AfterAFewQuickRetries`).
- **409** on push: `409 (Conflict)`; with `--skip-duplicate`, exit 0.
- **`dotnet package search` exits 0 when the source refuses it**: scripts must read its output.
- **An unlisted version is still chosen for a version range.** Restore reads the flat container, which lists unlisted versions as upstream does: with 1.0.0 unlisted, `[1.0.0, )` still resolves to 1.0.0. Unlisting hides a version from search only.
- **A hard delete leaves the empty version folder** in the inherited file storage; the package files are gone and the restore fails.
- `dotnet add package` and `dotnet nuget delete` have no `--configfile` option, and `dotnet nuget delete` reads `NUGET_API_KEY` when `--api-key` is missing.

`nuget.exe` and Visual Studio may behave differently (prompts, retries, credential storage): not observed (#43).

## 4. Security evidence: mutation checks

The security tests were shown to fail when the protection they cover is removed. Each pull request below broke the server code on purpose, ran the targeted tests, saw them fail, then reverted the change. These runs are M records, not repeated for this report.

| Pull request | Mutants | Result |
| --- | --- | --- |
| #81 Basic authentication | cache ignores the active flag | caught |
| #82 deny by default | permission check replaced by `return true` | 48 of 100 authorization tests fail |
| #83 API key model | 8 mutants (secret prefix stored, comparison skipped for unknown keys, no debounce, no permission intersection, token logged, no rotation transaction, password-format rule off, `$` instead of `\z`) | all caught |
| #84, #86 admin CLI | wrong hash, deferred or missing transaction, no-op `disable`/`set-permissions`/`reset-password` | all caught |
| #85 proxy and HTTPS | trust-all restored, HTTPS check removed, scheme pinned before the check | 6, 11 and 1 tests fail |
| #88 API key authentication | 9 mutants (M1–M9) plus 4 throttling mutants | all caught except "key-path `Check` removed alone", which is covered by the verification slot (equivalent) |
| #89 web hardening | 18 mutants | 17 caught; 1 equivalent (`Uri` already refuses `*`) |
| #90 throttling | 19 mutants | all caught |
| #95 key commands | 6 mutants | all caught |
| #96 audit logging | 12 mutants | all caught |
| #101 end-to-end | anonymous download, write rule without header key, hard delete without `Admin`, revoked keys accepted | all caught by the real-client tests |
| #107 repeated attempts | de-duplication off, full store skipping, repetition delay removed | all caught, including by the real client |

The hygiene checks were proven to fail on a throwaway branch with one commit per violation (#68), and the CI on a deliberately failing test (#62).

## 5. Known limitations and open risks

The user-facing list is [known limitations](user/known-limitations.md); the reasons are in the [threat model](technical/threat-model.md#residual-risks). In short:

**Not validated**

- `nuget.exe` (Windows, Mono) and Visual Studio: never run (#43). Neither are Rider, VS Code, MSBuild-only builds, Paket, PowerShellGet, macOS or debuggers fetching symbols.
- TLS: only through the three proxy samples, by hand, once (#91). Tunnels never.
- The release path (GHCR push, attestations, GitHub Release) has never run (#47). No image has been published.
- Non-default package database and storage providers; MySQL is excluded from the build (#55).
- Automated backup and restore, online backup, the full #42 migration suite, and the image-to-image upgrade (N/A before a first release).
- Firefox and Safari for the web UI.
- Items listed as not verified in the merged pull requests and still open: a pull request from a fork (#61); GitHub rendering of the issue forms (#66); a port that really needs the three-way fallback (#72); the real no-echo password prompt (#84, #86); Windows Event Log and external log shipping (#96); Mermaid rendering on GitHub (#98); the Windows DPAPI credential path (#98); the timing equality of a repeated attempt (#107).

**Design limits and residual risks**

- Throttling counters, the verification cache and the failed-attempt fingerprints live in memory, per process: single node only, reset on restart.
- An attacker with many addresses can fill the fingerprint store (50 000 counted failures per hour by default); legitimate clients then fall back to counting each retry, which can trip the pair throttle again (#107).
- Clients behind one NAT share one throttle budget.
- The `dotnet` CLI does not honour `Retry-After`: a throttled restore fails within seconds instead of waiting (other clients not observed).
- ASP.NET Core data protection keys are kept in memory: web forms opened before a restart must be reloaded ([Docker](user/docker.md#data-protection-keys)).
- On Windows the security database file inherits the ACL of its folder; ElPaqueto does not manage Windows ACLs.
- `/stats` needs `Read`, not `Admin`; the list of configured services is off by default.
- The content security policy allows `'unsafe-inline'` and `'unsafe-eval'` (Alpine.js 2 and inline handlers): it does not stop injected script; the server-side Markdown and encoding rules do.
- HTTP Basic only: no web login, session or logout; `Read` sees every package; users cannot be deleted; the `admin` commands do not authenticate their caller.
- The logo's licence is not decided (#53).
- The inherited Docusaurus site and Helm chart are stale (#51).
- Upstream fixes, security fixes included, reach ElPaqueto only when ported; the baseline is `02b6706`.

**Defects and test reliability**

- `BusyServerClientTests.ARestoreWhileEveryVerificationSlotIsBusy_FailsWith503_AfterAFewQuickRetries` failed intermittently in full local runs during #107 (the client got the only verification slot). It passed in CI and in both local runs for this report. Tracked in #108.
- Temporary directories left behind by `ElPaquetoApplication` (#87) and `eng/port-upstream.sh` exiting 141 on large file sections (#100) are open.

## 6. Open issues

On 2026-11-14 these issues are open. Pull requests reference their issues with `Refs`, not `Closes`, so an open issue may already have merged work: the second column says which.

| Issue | Title | Merged work |
| --- | --- | --- |
| #5 | ADR: project identity and rebranding scope | #65 |
| #6 | ADR: authentication architecture | #75 |
| #7 | Add SECURITY.md and legal notices | #63 |
| #8 | Rewrite CONTRIBUTING and add issue forms and a pull-request template | #66 |
| #9 | Establish a code-quality baseline for ElPaqueto code | #67 |
| #10 | Enable repository security automation | #64 (branch and tag rulesets, `CODEOWNERS` not done) |
| #11 | Scaffold the ElPaqueto security module and test projects | #69 |
| #12 | Add typed configuration and validation for the security module | #74 |
| #13 | Document how upstream changes are watched and ported | #72 |
| #14 | Add repository hygiene checks to CI | #68 |
| #15 | Add the authentication database, user model and migrations | #78, #80 |
| #16 | Implement password hashing and verification | #79 |
| #17 | Replace the inherited Basic authentication handler | #81 |
| #19 | Harden forwarded headers, public base URL and HTTPS enforcement | #85 |
| #23 | Define the permission model and authorization policies | #82 |
| #24 | Apply a deny-by-default fallback policy with route-to-permission mapping | #82 |
| #25 | Enforce permissions on read endpoints | #82 |
| #27 | Secure the web UI, health check and statistics endpoints | #82, #89 |
| #28 | Return correct 401 and 403 responses compatible with NuGet clients | covered by #81, #82, #101; no pull request claims its read-only-mode criterion |
| #30 | Add authorization-matrix and endpoint-inventory security tests | partly by #82 and #101 |
| #31 | Define the API key model, token format and storage | #83 |
| #41 | Adapt inherited integration tests to deny-by-default | none |
| #42 | Add persistence and auth-schema migration tests | partly by #78, #83 (see U1–U4) |
| #43 | Verify nuget.exe and Visual Studio compatibility | none |
| #47 | CI: add a release workflow separate from validation | #97 (publish path not run) |
| #51 | Decide the fate of the inherited Docusaurus site and Helm chart | none |
| #52 | Produce the validation report and known-limitations list | this report |
| #53 | Add the project logo and apply ElPaqueto branding | #98 (licence undecided) |
| #55 | Restore the MySQL provider once Pomelo supports EF Core 10 | none |
| #87 | test: ElPaquetoApplication leaves temporary directories behind | none |
| #100 | fix: eng/port-upstream.sh exits 141 on file sections larger than the pipe buffer | none |
| #108 | test: BusyServerClientTests can fail intermittently in a full end-to-end run | none (opened with this report) |

## 7. Updating this report

Before a release ([releasing](maintainers/releasing.md)):

1. Re-run every L row on the release commit, or mark it **Not verified**.
2. Link the CI and Docker runs of the release commit, with their dates and toolchain versions.
3. Re-run the manual rows (M) that the release relies on, or mark them **Not verified**.
4. Review the limitations and the open issues.

A row is **Verified** only with evidence from the version it describes. A failing row blocks the release unless the criterion moves to the known limitations with its reason.

Reproduce the local runs:

```bash
dotnet build ElPaqueto.sln --configuration Release
dotnet test ElPaqueto.sln --configuration Release --no-build --filter "Category!=EndToEnd"
dotnet test tests/ElPaqueto.EndToEnd.Tests --configuration Release --no-build --filter "Category=EndToEnd"
bash eng/format-strict.sh
python3 eng/hygiene/run_all.py --range origin/develop..HEAD
```
