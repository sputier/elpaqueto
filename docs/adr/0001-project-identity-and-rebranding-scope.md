# 0001. Project identity and rebranding scope

- Status: Accepted
- Date: 2026-11-12
- Issues: #5 (this record), #58 (implementation)

This ADR records the decision implemented in #58: the total rename of the code
base from `BaGetter` (and the legacy `BaGet` prefix) to `ElPaqueto`. It also
records the identity rules that follow from that rename, so that later issues
do not re-decide them.

## Context

ElPaqueto is a fork of [BaGetter](https://github.com/bagetter/BaGetter) (MIT),
which itself descends from BaGet. The fork is based on upstream commit
`02b6706` (2026-04-08).

Before #58, every project, namespace, type, assembly, package ID, environment
variable and default file name carried the upstream name. A fork that keeps
those names cannot be told apart from upstream in stack traces, logs, package
feeds or container registries, and contributors cannot tell inherited code from
new code.

#58 renamed the whole code base with a scripted rename. The build succeeds, all
tests pass, and a database created before the rename opens with the renamed
host. What the rename deliberately left alone, and what it costs, was written
down nowhere. Without this record, contributors would not know why some `BaGet`
strings remain.

## Decision

### 1. Product name

The product is named **ElPaqueto**. Tagline: "The package dealer you can trust."

### 2. Total rename (done in #58)

Every project, folder, namespace, type, member and build file was renamed.

| Before | After |
|---|---|
| `src/BaGetter*`, `tests/BaGetter.*`, namespaces, types | `src/ElPaqueto*`, `tests/ElPaqueto.*` |
| `BaGetter.sln`, `BaGetter.dll` | `ElPaqueto.sln`, `ElPaqueto.dll` |
| `BaGetterOptions`, `BaGetterApplication`, `AddBaGetterOptions` | `ElPaquetoOptions`, `ElPaquetoApplication`, `AddElPaquetoOptions` |
| `BAGET_CONFIG_ROOT` | `ELPAQUETO_CONFIG_ROOT` |
| default SQLite file `bagetter.db` | `elpaqueto.db` |
| `window.baget` | `window.elpaqueto` |
| `/_content/BaGetter.Web/` | `/_content/ElPaqueto.Web/` |

The same rule covers every identifier form: `BaGetter` and `BaGet` become
`ElPaqueto` (for example `BaGetRegistrationIndexPageItem` becomes
`ElPaquetoRegistrationIndexPageItem`), camelCase `baGetter` becomes
`elPaqueto`, and lowercase `bagetter` and `baget` become `elpaqueto`. The
container image default connection string follows the default file name:
`Data Source=/data/db/elpaqueto.db`.

### 3. Intentionally preserved

These items still contain `BaGet` or `BaGetter` on purpose.

| Item | Reason |
|---|---|
| `LICENSE` keeps "Copyright 2023 BaGetter". `src/Directory.Build.props` credits "ElPaqueto contributors; based on BaGetter ... by the BaGetter community and Loic Sharma" and keeps the upstream copyright notice. | The MIT licence requires the copyright notice to be kept. The final legal wording is #7. |
| Lines citing upstream or original BaGet URLs (`github.com/bagetter/BaGetter/...`, `www.bagetter.com/...`, `github.com/loic-sharma/BaGet/...`). | They are history: links to the issues and pull requests that explain the code. Renaming them would break the links. |
| EF Core migration IDs (`[Migration("...")]`). | Existing databases record the applied migration IDs. Unchanged IDs let existing databases migrate unchanged. Entity names in model snapshots and designer files were rewritten consistently, which does not affect the schema. |
| The Azure Search analyzer name `baget-exact-match-analyzer`. | It is stored in existing Azure Search indexes. Renaming it would break them. |
| The legacy `bagetter.db` patterns in `.gitignore`. | Local databases created before the rename stay ignored. |
| Markdown files (contents only), `docs/` and `deployment templates/`. | Out of scope of #58. They are rewritten by #48, #49, #51 and #53. |
| User-visible links to the BaGetter documentation and issue tracker in the web UI. | UI branding is #53 (decision 6). |

### 4. NuGet package IDs

Library package IDs are `ElPaqueto.*`, which no longer collide with upstream
`BaGetter.*` packages. V1 publishes no library to nuget.org; the only published
artifact is the container image. Publishing `ElPaqueto.*` libraries is
revisited once a library has a real external consumer.

### 5. Container image

The image is `ghcr.io/OWNER/elpaqueto`: lowercase, where `OWNER` is a
placeholder for the publishing account or organization.

### 6. User-visible strings and UI branding

Page title, footer, help links and logo are delegated to #53. A visible "Based
on BaGetter" credit stays in the UI.

### 7. Documentation language

Documentation is written in English only and names the product "ElPaqueto".
"BaGetter" appears only for attribution, upstream porting and migration.

### 8. Upstream relationship: watch and port

`git merge upstream/main` is no longer possible: every file conflicts. Upstream
commits are ported instead: exported with `git format-patch`, mapped with a
path-mapping script, applied with `git am -3`, and reviewed in a pull request.
The procedure and the script are #13. The baseline is upstream `02b6706`
(2026-04-08).

### 9. Migration from BaGetter

The database schema and the configuration keys are unchanged. An operator moving
from BaGetter to ElPaqueto:

1. Renames the environment variable `BAGET_CONFIG_ROOT` to
   `ELPAQUETO_CONFIG_ROOT`, if it is set.
2. If the deployment relied on the default SQLite file, either renames
   `bagetter.db` to `elpaqueto.db` (in the container image:
   `/data/db/bagetter.db` to `/data/db/elpaqueto.db`), or sets
   `Database:ConnectionString` explicitly to the existing file. Deployments that
   already set `Database:ConnectionString` need no change.
3. Applies the authentication changes, which are documented separately (#35).

The host does not fall back to `bagetter.db` when `elpaqueto.db` is missing. A
silent fallback would hide configuration drift; the rename is documented
instead.

## Consequences

- One coherent identity: assemblies, namespaces, package IDs, image name and
  configuration all say ElPaqueto, and nothing can be confused with an upstream
  artifact.
- Cheap merges from upstream are lost. Each upstream change is ported by hand,
  with the mapping script for the mechanical part (#13).
- Upstream security fixes no longer arrive through a merge. Someone must watch
  upstream releases and security advisories and port the fixes (#13).
- Existing BaGetter deployments need the migration steps of decision 9.
- Effects on other issues:
  - #6 places security code under `ElPaqueto.*` without any upstream-diff
    constraint.
  - #7 writes the attribution text.
  - #13 documents the porting procedure and provides the mapping script.
  - #44 and #47 use the image name of decision 5.
  - #48, #49 and #51 rewrite the documentation.
  - #53 applies the UI branding.

## Alternatives

- **No rename.** Merges from upstream stay cheap, but upstream names remain in
  every assembly, stack trace and package ID, and contributors cannot tell
  inherited code from new code. Rejected.
- **Hybrid: rename only the product layer** (host, UI, image) and keep the
  `BaGetter.*` libraries. Most merges stay cheap, but the identity is mixed for
  good, and the renamed layer conflicts on every merge anyway. Rejected.
- **Total rename (chosen).** One coherent identity and no confusion with
  upstream artifacts. The trade-off: cheap merges are lost, every upstream change
  costs a manual port (reduced by the mapping script), and upstream security
  fixes must be watched for actively because they no longer arrive through a
  merge.
