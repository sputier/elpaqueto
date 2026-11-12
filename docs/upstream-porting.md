# Porting upstream changes

ElPaqueto is based on [BaGetter](https://github.com/bagetter/BaGetter). The
total rename recorded in [ADR 0001](adr/0001-project-identity-and-rebranding-scope.md)
touched every file, so upstream changes can no longer be merged. This document
describes how upstream is watched and how its changes are ported.

## 1. Purpose and model

Watch and port, never merge. `git merge upstream/main` conflicts on every file
and must not be used. Instead, selected upstream commits are exported as
patches, mapped to ElPaqueto names with `eng/port-upstream.sh`, applied with
`git am -3`, and reviewed in a pull request like any other change.

The upstream remote is used for fetching only. Nothing is ever pushed to it.

```sh
git remote add upstream https://github.com/bagetter/BaGetter.git
git remote set-url --push upstream DISABLED
git fetch upstream
```

`set-url --push` makes an accidental `git push upstream` fail.

## 2. Baseline record

The baseline is the last upstream commit whose changes are in ElPaqueto. Every
port pull request updates this section in the same pull request.

| Last upstream commit ported | Date | Upstream reference |
|---|---|---|
| `02b6706` | 2026-04-08 | `main`, "Mirrored package identification (#219)", 3 commits after tag `v1.6.5` |

Upstream commits after the baseline that were deliberately not ported:

| Upstream commit | Reason |
|---|---|
| (none yet) | |

## 3. Finding new changes

```sh
git fetch upstream --tags
git log --oneline --no-merges 02b6706..upstream/main   # replace 02b6706 with the current baseline
git tag --contains 02b6706 --list 'v*'                 # upstream releases after the baseline
```

Also watch:

- upstream releases: <https://github.com/bagetter/BaGetter/releases>;
- upstream security advisories:
  <https://github.com/bagetter/BaGetter/security/advisories>. Watching the
  repository on GitHub with "Security alerts" (or "All activity") enabled
  sends a notification for each one.

Cadence: at least once a quarter, and immediately on any upstream security
advisory or security release.

Which changes to port:

- By default, port upstream releases: one pull request per release tag.
- Port `main` commits ahead of a release only for security fixes or for bugs
  ElPaqueto hits. A security fix gets its own pull request.
- Each upstream commit stays a separate commit in the port pull request.

## 4. Porting procedure

Run these commands from the root of an ElPaqueto clone that has the `upstream`
remote fetched. The script needs the upstream blobs of that clone (see
[How the script works](#how-the-script-works)). Keep the patch files outside
the working tree so that they are never committed.

```sh
git fetch upstream
git switch -c port/upstream-<short-sha> develop    # <short-sha>: last upstream commit ported

rm -rf ../port-patches
git format-patch -o ../port-patches <baseline>..<sha>   # a range of upstream commits
# or: git format-patch -o ../port-patches -1 <sha>       # a single upstream commit

eng/port-upstream.sh ../port-patches/*.patch
git am -3 ../port-patches/*.patch
```

`eng/port-upstream.sh` rewrites the patches in place (`-o <dir>` writes them to
another folder instead) and prints, for each file section, whether it was
`mapped`, mapped for its `path only`, or left `unchanged`.

On Windows, either run the whole procedure in Git Bash, or run it from
PowerShell and call the script through the Git Bash that ships with Git for
Windows. PowerShell does not expand `*.patch` for `git am`, so pass the file
list explicitly:

```powershell
& "C:\Program Files\Git\bin\bash.exe" eng/port-upstream.sh ../port-patches/*.patch
git am -3 (Get-ChildItem ../port-patches/*.patch).FullName
```

When `git am -3` stops on a conflict:

- Resolve the conflicted files, `git add` them, then `git am --continue`.
- Hunks on files ElPaqueto deleted show as `CONFLICT (modify/delete)`. Run
  `git rm <path>` to keep the deletion, then `git am --continue`. If every
  hunk of the commit is on deleted files, run `git am --skip` instead and add
  the commit to the skipped list of the baseline record.
- `git am --abort` returns to the state before `git am`.

Then update the baseline record (section 2), push the branch and open a pull
request into `develop`. The pull request description lists the upstream
commits ported, the conflicts and how they were resolved, the hunks dropped
(section 5), and the outcome of the security checklist (section 6).

### How the script works

`eng/port-upstream.sh` applies the rename rules of #58, in this order, to the
paths and to the content of the diff:

| Upstream | ElPaqueto |
|---|---|
| `BaGetter` | `ElPaqueto` |
| `BaGet` | `ElPaqueto` |
| `bagetter` | `elpaqueto` |
| `BAGET_CONFIG_ROOT` | `ELPAQUETO_CONFIG_ROOT` |
| `baGetter` (camelCase) | `elPaqueto` |
| `baget` (whole word, such as `window.baget`) | `elpaqueto` |

Exclusions, following the preserved items of ADR 0001:

- Lines containing an upstream URL (an `http` or `https` URL that contains
  `baget`, in any case, or `loic-sharma`) are left unchanged.
- Lines containing `baget-exact-match-analyzer` are left unchanged: the
  analyzer name is stored in existing Azure Search indexes.
- Lines containing `[Migration("` are left unchanged: EF Core migration IDs
  must not change. A new upstream migration therefore keeps its ID while its
  class and file names are renamed, which EF Core accepts.
- Sections under `docs/` and `deployment templates/` are left unchanged, paths
  and content.
- Markdown files (`*.md`) elsewhere get their path mapped (for example
  `src/BaGetter.Core/readme.md` becomes `src/ElPaqueto.Core/readme.md`) and
  their content left unchanged, as #58 did.
- Binary patches get their path mapped and their data left unchanged.

These path exclusions follow the paths #58 left unrenamed. When #48, #49, #51
or #53 rename or rewrite them, update the exclusions in the script and in this
list in the same pull request.

Other behaviour:

- CRLF line endings are converted to LF in mapped sections, because ElPaqueto
  stores every text file with LF while a few upstream files
  (`Directory.Packages.props`, `Dockerfile`) use CRLF.
- The commit message and the author are kept unchanged. The script appends an
  `Upstream-Commit: <full sha>` trailer to the commit message.
- For each mapped file, the script writes a mapped copy of the upstream
  pre-image and post-image blobs into the local repository and points the
  patch `index` line at them. This is what lets the three-way fallback of
  `git am -3` work when the ElPaqueto file has drifted from upstream. Without
  the upstream blobs (the `upstream` remote not fetched, or the script run
  outside a clone), the `index` lines are kept and `git am` can only apply the
  patches without the three-way fallback.
- Running the script twice on the same patch changes nothing.

Limits: a line that matches an exclusion is skipped as a whole, so an
identifier on the same line as an upstream URL is not renamed. Lines that #58
or later changes rewrote by hand (such as the image source label in the
`Dockerfile` or `PackageProjectUrl` in `src/Directory.Build.props`) conflict
and are resolved by keeping the ElPaqueto version. Review the mapped diff
before pushing.

The script is tested by `eng/tests/port-upstream.test.sh`, which runs on Linux
and on Windows (Git Bash) and in CI:

```sh
bash eng/tests/port-upstream.test.sh
```

## 5. Files ElPaqueto deleted or replaced

Hunks on these paths are dropped, or resolved by keeping the ElPaqueto version.
The pull request says which and why.

| Upstream path | ElPaqueto state |
|---|---|
| `.github/workflows/main.yml`, `release.yml`, `release-helm.yml`, `deploy_docs.yml` | Deleted (#2), replaced by `.github/workflows/ci.yml` (#3). Drop the hunks. |
| `tests/BaGetter.Tests/BaGetter - Backup.Tests.csproj` | Deleted (#4). Drop the hunks. |
| `CODE_OF_CONDUCT.md` | Deleted (#7): it routed reports to an upstream maintainer. Drop the hunks. |
| `.github/ISSUE_TEMPLATE/Bug_report.md`, `Feature_request.md` | Deleted (#8), replaced by issue forms. Drop the hunks. |
| `deployment templates/chart/bagetter/` (Helm chart) and `docs/` (Docusaurus site) | Still present and not mapped by the script. If #51 removes them, drop the hunks. |
| `src/BaGetter.Web/Authentication/NugetBasicAuthenticationHandler.cs` (inherited authentication handler), its unit tests `tests/BaGetter.Web.Tests/Authentication/NugetBasicAuthenticationHandlerTests.cs`, and `src/BaGetter/IServiceCollectionExtensions.cs` (`AddNugetBasicHttpAuthentication`, `AddNugetBasicHttpAuthorization`) | Deleted (#17), replaced by `src/ElPaqueto.Security/Authentication/`. Drop the hunks and re-implement any upstream security fix in the new code. |
| `tests/BaGetter.Tests/NugetAuthenticationIntegrationTests.cs`, `tests/BaGetter.Tests/NugetAllowAnonymousAuthenticationIntegrationTests.cs` | Deleted (#17), replaced by `tests/ElPaqueto.Tests/BasicAuthenticationIntegrationTests.cs`. The "anonymous allowed" expectation is inverted: anonymous requests now get 401. Drop the hunks. |
| `src/BaGetter.Database.MySql/` (MySQL provider) | Still in the tree but excluded from the build: not in `ElPaqueto.sln` and not referenced by the host (#55). Hunks apply but are neither built nor tested; the pull request says so. |

The [upstream touch map](technical/upstream-touch-map.md) lists every inherited
file ElPaqueto changed or deleted, with the reason: hunks on those files are
the ones most likely to conflict. `docs/technical/upstream-test-changes.md`
(#41) will list the upstream tests ElPaqueto changed; it does not exist yet.

## 6. Security review checklist

Every port pull request answers each item:

- **Authentication and authorization surface:** changes in `Authentication/`
  folders (`src/ElPaqueto.Core/Authentication/`,
  `src/ElPaqueto.Web/Authentication/`), `[Authorize]` and `[AllowAnonymous]`
  attributes, and the middleware order in `Startup.Configure`
  (`src/ElPaqueto/Startup.cs`).
- **New or changed endpoints:** `src/ElPaqueto.Web/ElPaquetoEndpointBuilder.cs`,
  `src/ElPaqueto.Web/Routes.cs`, controllers (`src/ElPaqueto.Web/Controllers/`)
  and Razor pages (`src/ElPaqueto.Web/Pages/`). Follow
  [Adding a protected endpoint](technical/adding-a-protected-endpoint.md):
  a row in the permission table, the authorization matrix, and the endpoint
  inventory tests passing.
- **GET endpoints with side effects**, such as mirroring or counters.
- **Dependency bumps** in `Directory.Packages.props`.
- **New configuration options**, especially anything that enables anonymous
  access.
- **Logging changes** that could expose secrets or credentials.

## 7. Attribution

`git am` keeps the original upstream author as the commit author; the person
porting is the committer. The script adds an `Upstream-Commit: <full sha>`
trailer to each ported commit, so the origin of every ported change can be
listed with:

```sh
git log --format='%h %(trailers:key=Upstream-Commit,valueonly,separator=)' develop
```

Do not squash a port pull request: squashing would replace the upstream
authors and merge the trailers.

## 8. Verification

All of the following are green before a port pull request is merged:

- `dotnet build` and `dotnet test` (unit and integration tests), as run by CI;
- the endpoint inventory tests of `tests/ElPaqueto.Tests/AuthorizationIntegrationTests.cs`
  (part of `dotnet test`);
- the end-to-end suite (#36 to #40), once it exists;
- a Docker build: `docker build .`;
- `bash eng/tests/port-upstream.test.sh`, when the pull request changes the
  script or its fixtures.
