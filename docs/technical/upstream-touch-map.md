# Upstream touch map

This map lists the files ElPaqueto inherited from
[BaGetter](https://github.com/bagetter/BaGetter) and then changed or deleted.
It tells whoever ports an upstream change (see
[Porting upstream changes](../upstream-porting.md)) where a mapped upstream
patch may not apply cleanly, and why ElPaqueto differs there. Files are
referenced by path, never by line number.

Kept current by every pull request that touches inherited files.

Upstream test changes: see `upstream-test-changes.md` (#41) once it exists.

## How it was produced

The comparison is not against raw upstream but against the **mapped upstream
baseline**: the upstream tree at the baseline commit, with the #58 rename rules
applied by `eng/port-upstream.sh`, exactly as a port would see it. A file that
differs only by the rename is therefore not listed.

| | |
|---|---|
| Upstream baseline | `02b6706` (see section 2 of [Porting upstream changes](../upstream-porting.md#2-baseline-record)) |
| ElPaqueto revision compared | `develop` of 2026-11-12 with the documentation and branding changes of #48 to #53 |
| Line endings | CR at end of line ignored (`--ignore-cr-at-eol`) |
| Renames | not detected (`--no-renames`): a moved file shows as deleted plus added |

Steps:

1. Build a commit whose tree is the upstream baseline tree and whose parent has
   the empty tree, and export it with `git format-patch`: one patch that adds
   every upstream file.
2. Map that patch with `eng/port-upstream.sh`.
3. Apply the mapped patch to an empty index (`git apply --cached`) and write
   the tree: this is the mapped upstream baseline.
4. Compare that tree with the ElPaqueto revision, then read the history of each
   changed file since the baseline (`git log --follow` crosses the #58 rename)
   to find the issues behind the change.

Limits of the comparison:

- `docs/` and `deployment templates/` are not mapped by the script (section 4
  of the porting guide). They are left out of the tables below; upstream
  patches on them apply to unrenamed content.
- Markdown files keep their upstream content (only their path is mapped), as
  #58 did, so the counts for `readme.md` and `CONTRIBUTING.md` are ElPaqueto
  edits only.
- Lines #58 kept unchanged on purpose show up as differences, because the
  script maps them: `LICENSE` (the upstream copyright line) and the package
  metadata in `src/Directory.Build.props`. Upstream hunks on those lines
  conflict; keep the ElPaqueto version.

## How to regenerate

From the root of an ElPaqueto clone with the `upstream` remote fetched, in Git
Bash or bash on Linux:

```sh
git fetch upstream
bash eng/regenerate-touch-map.sh ../touch-map-data            # -b <baseline> -r <rev> to override
```

The script works in a temporary shared clone and writes only to the output
folder: `name-status.txt` (added, deleted, modified paths),
`numstat-modified.txt` (change sizes), `diff-modified.patch`, `file-logs.txt`
(commits per file), `commit-issues.txt` (issue references per commit) and
`port-upstream.log`. Then update the tables below by hand.

The same steps without the script:

```sh
repo=$(pwd)
git clone -q --shared --no-checkout "$repo" ../touch-map-clone
cd ../touch-map-clone
empty_tree=$(git hash-object -t tree -w --stdin </dev/null)
empty_commit=$(git commit-tree "$empty_tree" -m "empty tree")
base_commit=$(git commit-tree "02b6706^{tree}" -p "$empty_commit" -m "upstream baseline tree")
mkdir ../touch-map-patch ../touch-map-mapped
git format-patch -1 --binary --no-renames -o ../touch-map-patch "$base_commit"
bash "$repo/eng/port-upstream.sh" -o ../touch-map-mapped ../touch-map-patch/*.patch

GIT_INDEX_FILE=../touch-map.index git read-tree --empty
GIT_INDEX_FILE=../touch-map.index git apply --cached --whitespace=nowarn ../touch-map-mapped/*.patch
mapped_tree=$(GIT_INDEX_FILE=../touch-map.index git write-tree)

head=$(git -C "$repo" rev-parse HEAD)
git diff --no-renames --name-status "$mapped_tree" "$head"
git diff --no-renames --ignore-cr-at-eol --numstat --diff-filter=M "$mapped_tree" "$head" \
  -- . ':(exclude)docs/' ':(exclude)deployment templates/'
git -C "$repo" log --oneline --follow 02b6706..HEAD -- <path>   # for each modified file
```

`eng/port-upstream.sh` currently stops with exit code 141 (SIGPIPE) on a file
section larger than the pipe buffer, which the whole-tree patch contains (for
example the vendored `alpine-ie11.js`). `eng/regenerate-touch-map.sh` runs a
temporary copy of the script that tolerates that one pipe; the mapping rules
are unchanged.

## Modified inherited files

48 inherited files differ from the mapped upstream baseline (outside `docs/`
and `deployment templates/`). Change size is insertions/deletions against the
mapped baseline.

### Host: `src/ElPaqueto`

| Inherited file | Change size (+/-) | Reason (issues) |
|---|---|---|
| `src/ElPaqueto/Program.cs` | +156/-4 | Non-zero exit code on invalid configuration or failed start (#12, #15); `admin` command group and API key commands (#20, #33); BaGetter upgrade checks and configuration load failures (#59, #93); no exception messages on startup errors (#35, #93) |
| `src/ElPaqueto/Startup.cs` | +10/-21 | Security module wiring replacing the inherited Basic authentication (#11, #17); forwarded headers from known proxies only and HTTPS (#19); deny-by-default authorization, health checks as an authorized endpoint (#23, #24); security headers and configured CORS (#29); MySQL provider removed (#1) |
| `src/ElPaqueto/ConfigureElPaquetoServer.cs` | +1/-14 | Inherited allow-all CORS policy removed, replaced by a configured policy (#29); rename leftovers (#58) |
| `src/ElPaqueto/appsettings.json` | +4/-18 | Legacy `ApiKey` and `Authentication` settings removed (#35); `ListConfiguredServices` off by default (#27); audit log category (#22) |
| `src/ElPaqueto/ElPaqueto.csproj` | +2/-2 | .NET 10 and MySQL reference removed (#1); reference to `ElPaqueto.Security` (#11) |
| `src/ElPaqueto/ValidateElPaquetoOptions.cs` | +0/-1 | MySQL no longer an accepted database type (#1) |

### Web: `src/ElPaqueto.Web`

| Inherited file | Change size (+/-) | Reason (issues) |
|---|---|---|
| `src/ElPaqueto.Web/Pages/Package.cshtml.cs` | +1/-3 | Package README rendered without HTML or unsafe URLs, via `Markdown/PackageMarkdown.cs` (#27) |
| `src/ElPaqueto.Web/ElPaquetoUrlGenerator.cs` | +1/-1 | Manifest download URL uses the manifest route (#19) |
| `src/ElPaqueto.Web/Extensions/IServiceCollectionExtensions.cs` | +0/-1 | `using` of the deleted inherited authentication namespace removed (#17) |
| `src/ElPaqueto.Web/ElPaqueto.Web.csproj` | +1/-1 | .NET 10 (#1) |
| `src/ElPaqueto.Web/Pages/Shared/_Layout.cshtml` | +13/-3 | ElPaqueto logo, favicon set, documentation link and BaGetter credit (#53) |
| `src/ElPaqueto.Web/Pages/Index.cshtml` | +2/-2 | Help links to the ElPaqueto documentation and issues instead of upstream (#53) |
| `src/ElPaqueto.Web/Pages/Package.cshtml` | +1/-1 | Same, on the "package not found" page (#53) |
| `src/ElPaqueto.Web/wwwroot/css/site.css` | +39/-0 | Header logo and footer link styles, appended at the end of the file (#53) |
| `src/ElPaqueto.Web/wwwroot/favicon.ico` | binary | ElPaqueto favicon (#53); keep the ElPaqueto file on conflict |

### Core, storage and database providers

| Inherited file | Change size (+/-) | Reason (issues) |
|---|---|---|
| `src/ElPaqueto.Core/ElPaqueto.Core.csproj` | +1/-1 | .NET 10 (#1) |
| `src/ElPaqueto.Aliyun/ElPaqueto.Aliyun.csproj` | +1/-1 | .NET 10 (#1) |
| `src/ElPaqueto.Aws/ElPaqueto.Aws.csproj` | +1/-1 | .NET 10 (#1) |
| `src/ElPaqueto.Azure/ElPaqueto.Azure.csproj` | +1/-1 | .NET 10 (#1) |
| `src/ElPaqueto.Gcp/ElPaqueto.Gcp.csproj` | +1/-1 | .NET 10 (#1) |
| `src/ElPaqueto.Tencent/ElPaqueto.Tencent.csproj` | +1/-1 | .NET 10 (#1) |
| `src/ElPaqueto.Database.MySql/ElPaqueto.Database.MySql.csproj` | +1/-1 | .NET 10 (#1); the project is excluded from the solution and the host (#1, #55) |
| `src/ElPaqueto.Database.PostgreSql/ElPaqueto.Database.PostgreSql.csproj` | +1/-1 | .NET 10 (#1) |
| `src/ElPaqueto.Database.SqlServer/ElPaqueto.Database.SqlServer.csproj` | +1/-1 | .NET 10 (#1) |
| `src/ElPaqueto.Database.Sqlite/ElPaqueto.Database.Sqlite.csproj` | +1/-1 | .NET 10 (#1) |

### Tests

| Inherited file | Change size (+/-) | Reason (issues) |
|---|---|---|
| `tests/ElPaqueto.Tests/Support/ElPaquetoApplication.cs` | +124/-3 | Each test host gets its own security database (#15); test users for Basic authentication (#17); API key on write routes (#26, #32) |
| `tests/ElPaqueto.Tests/ElPaqueto.Tests.csproj` | +7/-1 | .NET 10 (#1); controllable clock for throttling tests (#18); shared log capture for audit tests (#22) |
| `tests/ElPaqueto.Web.Tests/Pages/PackageModelFacts.cs` | +47/-0 | Safe README rendering tests (#27) |
| `tests/ElPaqueto.Web.Tests/ElPaqueto.Web.Tests.csproj` | +1/-1 | .NET 10 (#1) |
| `tests/ElPaqueto.Core.Tests/ElPaqueto.Core.Tests.csproj` | +1/-1 | .NET 10 (#1) |
| `tests/ElPaqueto.Protocol.Tests/ElPaqueto.Protocol.Tests.csproj` | +1/-1 | .NET 10 (#1) |
| `tests/Directory.Build.props` | +3/-0 | Imports the strict build baseline `eng/ElPaqueto.Build.props` (#9) |

### Samples

| Inherited file | Change size (+/-) | Reason (issues) |
|---|---|---|
| `samples/ElPaqueto.Protocol.Samples.Tests/ElPaqueto.Protocol.Samples.Tests.csproj` | +1/-1 | .NET 10 (#1) |
| `samples/ElPaquetoWebApplication/ElPaquetoWebApplication.csproj` | +1/-1 | .NET 10 (#1) |

### Build and repository files

| Inherited file | Change size (+/-) | Reason (issues) |
|---|---|---|
| `Dockerfile` | +100/-49 | .NET 10 base images (#1); rename and image labels (#58); security database on the data volume (#15); hardened image (#44) |
| `Directory.Packages.props` | +23/-18 | .NET 10 packages and transitive pinning of a vulnerable dependency (#1); test clock package (#18) |
| `ElPaqueto.sln` | +14/-7 | MySQL provider removed from the solution (#1); `ElPaqueto.Security` and its test project added (#11) |
| `global.json` | +1/-1 | .NET 10 SDK (#1) |
| `src/Directory.Build.props` | +6/-3 | Package authors, copyright and project URL (#58); strict build baseline import (#9) |
| `.devcontainer/devcontainer.json` | +1/-1 | .NET 10 SDK image (#1) |
| `.dockerignore` | +24/-30 | Build context turned into an allow-list (#44) |
| `.editorconfig` | +25/-0 | Strict analyzer rules for ElPaqueto-authored code (#9); EF Core migrations marked as generated (#15) |
| `.gitattributes` | +14/-0 | LF for ElPaqueto-authored files (#9); byte-exact port script fixtures (#13); LF for container files (#44); `.ico` as binary (#53) |
| `.gitignore` | +9/-0 | Legacy `bagetter.db` files still ignored after the rename (#58); Compose secrets folder (#45); `auth.db` created when running from source (#50) |
| `.github/PULL_REQUEST_TEMPLATE.md` | +24/-4 | Rewritten pull request template (#8) |
| `CONTRIBUTING.md` | +173/-9 | Rewritten contributing guide (#8): prerequisites (#1), CI (#2, #3), code-quality baseline (#9), repository layout (#11), container checks (#46), running from source with a first administrator, technical documentation and logo use (#50, #53) |
| `readme.md` | +80/-56 | Rewritten for ElPaqueto (#48): features, status, quick start, production, documentation, origin and licence; logo banner (#53) |
| `LICENSE` | +2/-1 | ElPaqueto copyright line added (#7); the upstream copyright line is kept as is (#58), see the limits above |

## Deleted inherited files

Hunks on these paths are dropped, as described in section 5 of
[Porting upstream changes](../upstream-porting.md#5-files-elpaqueto-deleted-or-replaced).
Paths are shown as upstream names them.

| Upstream path | Deleted by | Replacement |
|---|---|---|
| `.github/workflows/main.yml` | #3 | `.github/workflows/ci.yml` |
| `.github/workflows/release.yml`, `release-helm.yml`, `deploy_docs.yml` | #2 | none |
| `.github/ISSUE_TEMPLATE/Bug_report.md`, `Feature_request.md` | #8 | issue forms in `.github/ISSUE_TEMPLATE/` |
| `CODE_OF_CONDUCT.md` | #7 | none (it routed reports to an upstream maintainer) |
| `tests/BaGetter.Tests/BaGetter - Backup.Tests.csproj` | #4 | none (stale, not in the solution) |
| `src/BaGetter.Web/Authentication/NugetBasicAuthenticationHandler.cs` | #17 | `src/ElPaqueto.Security/Authentication/` |
| `src/BaGetter/IServiceCollectionExtensions.cs` | #17 | `src/ElPaqueto.Security/` (`AddElPaquetoSecurity`) |
| `tests/BaGetter.Web.Tests/Authentication/NugetBasicAuthenticationHandlerTests.cs` | #17 | `tests/ElPaqueto.Security.Tests/` |
| `tests/BaGetter.Tests/NugetAuthenticationIntegrationTests.cs`, `NugetAllowAnonymousAuthenticationIntegrationTests.cs` | #17 | `tests/ElPaqueto.Tests/BasicAuthenticationIntegrationTests.cs` |

That is 13 files. `src/BaGetter.Database.MySql/` is not deleted: it stays in
the tree, excluded from the build (#1, #55).

## ElPaqueto-only areas

258 files exist only in ElPaqueto. Upstream patches never touch them, but they
are where ported behaviour may need to be re-implemented:

- `src/ElPaqueto.Security/**` and `tests/ElPaqueto.Security.Tests/**`:
  authentication, authorization, API keys, throttling, audit, security
  headers, CORS and forwarded headers.
- New files inside inherited projects: `src/ElPaqueto/BaGetterUpgradeChecks.cs`,
  `src/ElPaqueto.Web/Markdown/PackageMarkdown.cs`,
  `tests/ElPaqueto.Web.Tests/Pages/PackageMarkdownFacts.cs`, and the security,
  upgrade and web UI integration tests in `tests/ElPaqueto.Tests/`
  (including `Support/`).
- `eng/**`: build baseline, Docker smoke test, hygiene checks, the port script
  and their tests.
- `.github/**`: CI, CodeQL, dependency review and Docker workflows, issue
  forms, Dependabot configuration.
- `docs/user/**`, `docs/technical/**`, `docs/adr/**`, `docs/maintainers/**`,
  `docs/upstream-porting.md`.
- `samples/reverse-proxy/**`, `docker-compose.example.yml`.
- `assets/branding/**` (the logo) and the web UI images in
  `src/ElPaqueto.Web/wwwroot/images/elpaqueto-*.png`.
- Root files: `SECURITY.md`, `THIRD-PARTY-NOTICES.md`, `.gitleaksignore`,
  `.trivyignore`.
