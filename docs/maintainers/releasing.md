# Releasing

A release of ElPaqueto is a version tag on `main`. Pushing the tag starts
`.github/workflows/release.yml`, which validates the tagged commit, waits for a
maintainer's approval and then publishes:

- the container image `ghcr.io/<owner>/elpaqueto`, for `linux/amd64` and
  `linux/arm64`;
- its attestations: build provenance and one SPDX SBOM per platform;
- a GitHub Release whose notes come from `CHANGELOG.md`.

Besides releases, a maintainer can publish a [development image](#development-images)
of `develop` or `main` on demand. Nothing else is published: no NuGet package,
no image on Docker Hub or on any other registry, no Helm chart. The only
credential is the workflows' `GITHUB_TOKEN`.

## Versioning

ElPaqueto follows [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).

- **Major**: a change that requires an action from operators, such as a
  removed or renamed setting, a database change that cannot be rolled back, or
  a changed API or container contract (user, port, volume).
- **Minor**: a new feature or setting that keeps existing deployments working.
- **Patch**: fixes only, including security fixes and dependency updates.
- **Prerelease**: `X.Y.Z-<suffix>`, such as `1.0.0-rc.1`, to test a version
  before the final release.

The tag is `v` followed by the version. It is refused unless it matches
`vMAJOR.MINOR.PATCH` with an optional `-prerelease` suffix: no leading zeros,
no build metadata (`+...`), identifiers made of `[0-9A-Za-z-]`.

The image tags follow from the version:

| Release | Image tags |
|---|---|
| Stable `1.4.2` | `1.4.2`, `1.4`, `1`, `latest` |
| Prerelease `1.5.0-rc.1` | `1.5.0-rc.1` only, never `latest` |

The application is built with the same version (`dotnet publish -p
Version=...`, through the `Version` build argument of the `Dockerfile`), and
the image's `org.opencontainers.image.version` label carries it too. The
workflow checks both before it pushes anything.

## Changelog

`CHANGELOG.md` follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Every pull request with a user-visible change adds a line under
`## [Unreleased]`, in the `Added`, `Changed`, `Deprecated`, `Removed`, `Fixed`
or `Security` group.

The release notes are the section of the version, followed by the image
digest and the commands that pull and verify it, followed by the notes that
GitHub generates from the merged pull requests.

- A stable release needs a `## [X.Y.Z] - YYYY-MM-DD` section. The workflow
  fails if it is missing.
- A prerelease uses its own section when it exists, otherwise the
  `## [Unreleased]` section.
- An empty section fails the release.

## What is automated

The workflow has four stages. Each one runs only when the previous one
succeeded: a failure anywhere stops the release before anything is published.

1. **verify** (`contents: read`): tests the release scripts, then checks that
   the run comes from a version tag push or a manual run, that the tag has
   the expected format, points to the commit of the run and is reachable from
   `main`, and that the changelog has the section. For a real release, it
   checks that the repository variable `RELEASE_REPOSITORY` names this
   repository, so a fork never publishes.
2. **ci** and **docker** (`contents: read`): the full validation, by the same
   workflows as pull requests (`ci.yml`: hygiene, build and every test on
   Linux and Windows; `docker.yml`: image build on both architectures, smoke
   test, vulnerability scan, samples).
3. **rehearse** (`contents: read`): builds the image with the release
   version, checks the version and labels, runs the smoke test and the
   vulnerability scan on it, then pushes every platform with its attestations
   to a registry started on the runner (no credential) and checks the tags,
   the platforms, the SBOMs and the provenance. The release notes are written
   to the job summary.
4. **publish** (environment `release`, waits for approval): builds the image
   again, validates it the same way (version, smoke test, scan), pushes it to
   GHCR, attests its provenance (to the multi-platform index) and its SBOMs
   (to each platform image) with `actions/attest`, and creates the GitHub
   Release. This is the only job with write scopes: `contents: write` for the
   release, `packages: write` for GHCR, `id-token: write` and
   `attestations: write` for the attestations.

All the tooling is the Docker CLI (`docker buildx`, with a BuildKit image and
an SBOM generator pinned by digest), the GitHub CLI and actions owned by
GitHub pinned by commit SHA. The scripts live in `eng/release/` and run
locally the same way; `eng/tests/release.test.sh` tests the release decision
and the release notes.

The image also carries the attestations that BuildKit generates
(`--provenance=mode=max`, `--sbom`), visible with
`docker buildx imagetools inspect <image> --format '{{json .Provenance}}'` (or
`.SBOM`). In the GHCR package page, they show as `unknown/unknown` entries of
the index.

## Dry run

A manual run is a dry run by default. It runs stages 1 to 3 and never reaches
the `release` environment, so it publishes nothing and needs no approval.

```sh
# Rehearse the release of the current develop (version 0.0.0-dryrun.<run number>):
gh workflow run release.yml --ref develop

# Rehearse an existing tag (checks the tag and its changelog section too):
gh workflow run release.yml --ref v1.2.0
```

A dry run is worth doing after a change to the release workflow, the
`Dockerfile` or the scripts in `eng/release/`, and before tagging a release.

## Development images

`.github/workflows/publish-development-image.yml` publishes the current
`develop` or `main` to the same GHCR repository, to run the latest validated
code on a server before a release. It is started by hand only: like the
release, a workflow that can publish is never started by a push or a pull
request (hygiene check `publish-trigger`).

```sh
gh workflow run publish-development-image.yml --ref develop
```

- **Validation:** the same as a release. `ci.yml` and `docker.yml` run on the
  commit, then the build that is pushed is smoke-tested and scanned first.
- **Image:** `linux/amd64` and `linux/arm64`, with the provenance and SBOM
  attestations, and the version `0.0.0-dev.<run number>`, which sorts below
  every release.
- **Tags:** `develop` or `main`, which moves to each new development image of
  that branch, and `sha-<full commit SHA>`, which never moves. Never `latest`
  and never a version: `eng/release/resolve-development-image.sh` decides the
  tags and refuses any other event or branch, and
  `eng/tests/development-image.test.sh` tests it.
- **No approval and no GitHub Release:** starting the workflow is the
  maintainer's decision. It needs the `RELEASE_REPOSITORY` variable, like a
  release, so a fork never publishes.

On a server, pin a development image by digest (printed in the run summary),
not by the branch tag.

## One-time repository setup

These settings live outside the repository. Check them before the first
release; [repository-settings.md](repository-settings.md) records the
commands that verify them.

- [ ] **Environment `release`** (*Settings → Environments*): required
  reviewers (the maintainers), *Prevent self-review* off while there is a
  single maintainer, deployment branches and tags limited to the tag rule
  `v*`. No environment secret is needed.
- [ ] **Repository variable `RELEASE_REPOSITORY`** (*Settings → Secrets and
  variables → Actions → Variables*) set to `<owner>/<repository>`. Without it,
  real releases fail in `verify`; dry runs still work.
- [ ] **Tag ruleset** on `refs/tags/v*`: only maintainers can create, update
  or delete release tags.
- [ ] **Workflow permissions**: the default `GITHUB_TOKEN` permissions stay
  read-only; the publish job asks for its own scopes.
- [ ] **GHCR package** after the first release (*Packages → elpaqueto →
  Package settings*): visibility public, linked to the repository, and
  *Manage Actions access* gives this repository the write role. The first
  push creates the package as private.

## Cutting a release

1. Make sure `develop` is green and holds everything planned for the release.
2. Run a dry run on `develop` (see above) and check its summary.
3. Review [client compatibility](../user/client-compatibility.md) and the
   [known limitations](../user/known-limitations.md) against what the
   release contains.
4. Open a pull request from `develop` to `main`. In it, move the
   `## [Unreleased]` entries of `CHANGELOG.md` under a new
   `## [X.Y.Z] - YYYY-MM-DD` heading, leave an empty `## [Unreleased]` heading
   above it, and update the comparison links at the bottom. (For `0.1.0`
   this was done in advance; check the date.) An empty `Unreleased` section is
   fine: a dry run on a branch then reports "no release notes" instead of
   failing.
5. Merge it, then tag the merge commit on `main` with a signed, annotated tag
   and push the tag:

   ```sh
   git fetch origin
   git tag -s vX.Y.Z origin/main -m "ElPaqueto X.Y.Z"
   git push origin vX.Y.Z
   ```

6. Follow the run in the Actions tab. When `rehearse` has passed, the
   `publish` job waits for approval: review the rehearsal summary, then
   approve the deployment to `release`.
7. Check the result:

   ```sh
   gh release view vX.Y.Z
   docker buildx imagetools inspect ghcr.io/<owner>/elpaqueto:X.Y.Z
   # Provenance of the multi-platform image:
   gh attestation verify oci://ghcr.io/<owner>/elpaqueto:X.Y.Z --repo <owner>/<repository>
   # SBOM of one platform image (digest from the inspect command above):
   gh attestation verify oci://ghcr.io/<owner>/elpaqueto@sha256:<platform digest> \
     --repo <owner>/<repository> --predicate-type https://spdx.dev/Document/v2.3
   ```

8. Merge `main` back into `develop` so that the changelog heading reaches it.

A real release can also be started by hand on the tag, which is useful when a
transient failure stopped the tag's run before publishing anything:
`gh workflow run release.yml --ref vX.Y.Z -f dry-run=false`.

## When something goes wrong

- **Before approval** (verify, validation or rehearsal failed): nothing was
  published. Fix the cause on `develop`, then release a new version with a new
  tag; do not move the existing tag. If the tag itself is wrong (format,
  commit not on `main`), delete it with the tag ruleset's bypass and tag again.
- **During publish**: the image may be pushed without its attestations or its
  GitHub Release. If the cause is outside the repository (registry outage,
  missing permission), fix it and run the workflow again on the same tag. The
  image is built again, so its digest can differ: the new push replaces the
  image tags, and the release notes give the digest that the attestations
  cover. Creating the GitHub Release fails if one already exists for the tag.
  If the cause is in the repository, release a new patch version instead.
- **After the release** (a defect is found): release a new patch version. Do
  not delete or move the tag, the image tags or the release; mark the faulty
  release as such in its notes if needed.

## Not automated yet

- Release archives (framework-dependent `.zip` builds) are not produced: the
  container image is the supported distribution.
- Image signing beyond GitHub attestations (cosign) and NuGet libraries are
  out of scope for now.
