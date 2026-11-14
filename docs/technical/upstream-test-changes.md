# Upstream test changes

ElPaqueto inherited the BaGetter test suite and keeps it as the regression net for package behaviour. Under deny-by-default every request needs credentials, so the inherited tests had to be adapted. This page lists every inherited test file ElPaqueto changed, deleted or extended, with the reason and what to do with an upstream hunk on it when you port upstream changes (see [Porting upstream changes](../upstream-porting.md)).

The rules (issue #41):

- Inherited tests are adapted, never skipped, excluded by trait, commented out or loosened (a `401` is never accepted as success).
- Changes to an inherited test file are limited to client construction and credentials. Test bodies and assertions stay as upstream wrote them.
- Every adapted class has at least one test proving that the same request without credentials gets `401`, so the adaptation leaves no anonymous path.

Paths are given with their ElPaqueto names. Upstream names them `BaGetter.*`; `eng/port-upstream.sh` maps them.

## How the inherited integration tests authenticate

Almost no inherited integration test changed: the test host does the work.

`tests/ElPaqueto.Tests/Support/ElPaquetoApplication.cs`, the `WebApplicationFactory` of the integration tests, gives each host its own security database in its temporary folder and seeds a test user before the host starts. The user holds every permission and has an API key. `ConfigureClient` sends that key in `X-NuGet-ApiKey` from every client returned by `CreateClient()` and `CreateDefaultClient()`, the way `dotnet nuget push` does. `CreateAnonymousClient()` returns a client with no credential.

`tests/ElPaqueto.Tests/Support/TestUserExtensions.cs` (an ElPaqueto file) creates users and API keys with exact permissions, and clients that send exactly one credential. Issue #41 planned a separate `ElPaquetoTestIdentity.cs` for this; these two files play that role.

The default identity holds every permission, Admin included, rather than the Read and Publish that #41 recommended. The inherited tests that delete packages need Admin when packages are deleted for good, and a single default keeps the inherited files untouched. The missing-permission cases are covered by the [security suites](testing.md#security-suites), which create a user or key per permission.

## Changed inherited files

| File | Change | Reason | Porting an upstream hunk |
| --- | --- | --- | --- |
| `tests/ElPaqueto.Tests/Support/ElPaquetoApplication.cs` | Security database path, test user and API key seeding, authenticated default clients, `CreateAnonymousClient()`, `seedTestUser` parameter | Deny-by-default (#15, #17, #26, #32) | Apply upstream changes to the configuration dictionary and services by hand, keeping the security entries and the seeder. |
| `tests/ElPaqueto.Tests/ApiIntegrationTests.cs` | Class declared `partial` (one word) | The `401` checks of #41 live in `ApiIntegrationTests.Anonymous.cs` | Hunks apply; keep `partial` if a hunk touches the declaration. |
| `tests/ElPaqueto.Tests/ElPaquetoClientIntegrationTests.cs` | Class declared `partial` | `401` checks in `ElPaquetoClientIntegrationTests.Anonymous.cs` | Same. |
| `tests/ElPaqueto.Tests/NuGetClientIntegrationTests.cs` | Class declared `partial` | `401` checks in `NuGetClientIntegrationTests.Anonymous.cs` | Same. |
| `tests/ElPaqueto.Tests/MirrorIntegrationTests.cs` | Class declared `partial` | `401` checks in `MirrorIntegrationTests.Anonymous.cs`, for both the downstream and the upstream app | Same. |
| `tests/ElPaqueto.Web.Tests/Pages/PackageModelFacts.cs` | Tests added, none changed | Safe rendering of package READMEs (#27) | Hunks apply; keep the added tests. |
| `tests/ElPaqueto.Tests/ElPaqueto.Tests.csproj` | .NET 10, time provider for tests, shared log capture | #1, #18, #22 | Keep the ElPaqueto target and references. |
| `tests/ElPaqueto.Core.Tests/ElPaqueto.Core.Tests.csproj`, `tests/ElPaqueto.Protocol.Tests/ElPaqueto.Protocol.Tests.csproj`, `tests/ElPaqueto.Web.Tests/ElPaqueto.Web.Tests.csproj`, `samples/ElPaqueto.Protocol.Samples.Tests/ElPaqueto.Protocol.Samples.Tests.csproj` | Target framework | .NET 10 (#1) | Keep `net10.0`. |
| `tests/Directory.Build.props` | Imports `eng/ElPaqueto.Build.props` | Strict baseline for ElPaqueto projects (#9) | Keep the import. |

The other inherited test classes, `HostIntegrationTests` included, are unchanged: `git diff` against the #58 rename is empty for them. They run unchanged under deny-by-default because the default client is authenticated.

The `*.Anonymous.cs` files are ElPaqueto files; upstream never touches them. Each contains, for its class, the same requests sent without credentials and checked to get `401`:

| Class | `401` check |
| --- | --- |
| `ApiIntegrationTests` | `TheSameRequestWithoutCredentials_Returns401`: ten of the class's read requests (service index, search, autocomplete, versions, `.nupkg`, `.nuspec`, registrations, dependents, symbols), each answered `200` with the default client and `401` without credentials. |
| `ElPaquetoClientIntegrationTests` | `TheSameRequestsWithoutCredentials_Return401`: the ElPaqueto client SDK reads the service index and searches with the default client, and gets `401` without credentials. |
| `NuGetClientIntegrationTests` | `TheSameRequestWithoutCredentials_Returns401`: the official NuGet client reads the service index with the default client, and fails with `401` without credentials. |
| `MirrorIntegrationTests` | `TheSameRequestWithoutCredentials_Returns401OnTheDownstream`, and `AMirrorWithoutCredentials_GetsNothingFromTheUpstream`: a mirror whose upstream client sends no credential is refused by the upstream app (`401`) and finds nothing (`404`). The inherited mirror tests therefore pass only because the mirror authenticates. |

## Deleted inherited files

| File | Deleted by | Tests | Replaced by |
| --- | --- | --- | --- |
| `tests/ElPaqueto.Tests/NugetAuthenticationIntegrationTests.cs` | #17 | `AnonymousAccess_WhenAnonymousNotAllowed_ReturnsUnauthorized`, `ValidCredentialsAccess_WhenAnonymousNotAllowed_ReturnsOk`, `InvalidCredentialsAccess_WhenAnonymousNotAllowed_ReturnsUnauthorized` | `BasicAuthenticationIntegrationTests`: `NoCredentials_GetsAChallengeWithTheExpectedRealmAndNoRedirect`, `ValidCredentialsOfAnActiveUser_AreAccepted`, `WrongPasswordUnknownUserAndDisabledUser_GetByteIdenticalResponses` |
| `tests/ElPaqueto.Tests/NugetAllowAnonymousAuthenticationIntegrationTests.cs` | #17 | `AnonymousAccess_WhenAnonymousAllowed_ReturnsOk`, `Credentials_WhenAnonymousAllowed_ReturnsOk` | Anonymous access no longer exists (ADR 0002): the expectation is inverted by `NoCredentials_GetsAChallengeWithTheExpectedRealmAndNoRedirect`, and `LegacyAuthenticationSettingsTests` checks that the inherited authentication settings (`ApiKey`, `Authentication:*`) fail validation at startup instead of being silently ignored. Credentials: `ValidCredentialsOfAnActiveUser_AreAccepted`. |
| `tests/ElPaqueto.Web.Tests/Authentication/NugetBasicAuthenticationHandlerTests.cs` | #17 | `HandleAuthenticateAsync_AnonymousAllowed_ReturnsSuccessResult`, `_InvalidAuthorizationHeader_ReturnsFailResult`, `_InvalidCredentials_ReturnsFailResult`, `_NoAuthorizationHeader_ReturnsNoResult`, `_ValidCredentials_ReturnsSuccessResult` | The handler is replaced by `src/ElPaqueto.Security/Authentication/`. Anonymous mode: inverted, as above. Invalid header: `MalformedHeaders_AreRejectedWithA401NeverA500`, `BasicCredentialsParserTests`, `RequestCredentialSelectorTests.AMalformedBasicHeader_IsInvalid`. Invalid credentials: `WrongPasswordUnknownUserAndDisabledUser_GetByteIdenticalResponses`. No header: `NoCredentials_GetsAChallengeWithTheExpectedRealmAndNoRedirect`, `RequestCredentialSelectorTests.NoCredential_IsNone`. Valid credentials: `ValidCredentialsOfAnActiveUser_AreAccepted`. |
| `tests/BaGetter.Tests/BaGetter - Backup.Tests.csproj` | #4 | none (a stale project, not in the solution) | none |

Drop upstream hunks on deleted files, and re-implement any upstream security fix in `src/ElPaqueto.Security/` with its tests.

## Test counts

Test cases reported by `dotnet test`, per project. "Before" is the #58 rename (`fcd93e8`), the last revision with the inherited authentication tests; all of its tests are inherited. Issue #41 gives the same total, 350, but its table pairs the counts with the wrong projects; these are the counts measured on `fcd93e8`.

| Project | Before | Removed (above) | Inherited tests now | All tests now |
| --- | --- | --- | --- | --- |
| `ElPaqueto.Core.Tests` | 187 | 0 | 187 | 187 |
| `ElPaqueto.Protocol.Tests` | 59 | 0 | 59 | 59 |
| `ElPaqueto.Web.Tests` | 25 | 5 | 20 | 82 |
| `ElPaqueto.Tests` | 71 | 5 | 66 | 1677 |
| `ElPaqueto.Protocol.Samples.Tests` | 8 | 0 | 8 | 8 |
| `ElPaqueto.Security.Tests` | (ElPaqueto only) | | | 859 |
| Total | 350 | 10 | 340 | 2872 |

"Inherited tests now" counts the test cases of the classes that come from upstream, without the tests ElPaqueto added to them (the `*.Anonymous.cs` files, and the seven README tests #27 added to `PackageModelFacts`). Every inherited test that was not removed still runs: 350 − 10 = 340. Counted on 2026-11-15 on Windows, end-to-end tests excluded.

## Skipped tests

No test is skipped on Linux. On Windows, the tests marked `UnixFact` or `UnprivilegedUnixFact` in `tests/ElPaqueto.Security.Tests` are reported as skipped: they check Unix file modes and signals, which do not exist there, and they run on the Linux leg of the CI. `UnprivilegedUnixFact` is also skipped when the tests run as root, which reads files whatever their mode. These are ElPaqueto tests, not inherited ones; no inherited test is skipped anywhere.
