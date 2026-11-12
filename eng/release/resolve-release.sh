#!/usr/bin/env bash
# Decides what a run of the release workflow builds and publishes, and refuses anything else.
# Used by the "verify" job of .github/workflows/release.yml; tested by eng/tests/release.test.sh.
#
# Environment (the workflow passes the github context):
#   EVENT_NAME        push | workflow_dispatch (anything else is refused)
#   REF_TYPE          tag | branch
#   REF_NAME          tag or branch name
#   DRY_RUN           workflow_dispatch input: "true" (default when empty) or "false"
#   RUN_NUMBER        run number, used for the dry-run version of a branch
#   REPOSITORY_OWNER  owner of the repository (the image namespace, lowercased)
#   SHA               commit of the run
#   MAIN_REF          ref that a release commit must be reachable from (default: origin/main)
#   CHANGELOG         changelog file (default: CHANGELOG.md)
#
# Output: key=value lines on standard output, also appended to $GITHUB_OUTPUT when it is set:
#   dry_run, tag, version, prerelease, image, tags (space-separated image tags), notes_section
#
# Rules:
#   - a tag push always publishes (dry_run=false); a manual run is a dry run unless DRY_RUN=false;
#   - a real release must run on a tag (push of the tag, or manual run started on the tag);
#   - a tag is vMAJOR.MINOR.PATCH with an optional prerelease suffix (SemVer 2.0.0, no build
#     metadata, no leading zeros), points to SHA, and SHA is reachable from MAIN_REF;
#   - a dry run on a branch uses the version 0.0.0-dryrun.RUN_NUMBER and checks the Unreleased section;
#   - the changelog has a "## [VERSION]" section for a stable release; a prerelease uses its own
#     section when it exists, otherwise "## [Unreleased]".
set -euo pipefail

die() { printf 'release: %s\n' "$*" >&2; exit 1; }

numeric='(0|[1-9][0-9]*)'
identifier='([0-9A-Za-z-]+)'
version_pattern="^$numeric\\.$numeric\\.$numeric(-$identifier(\\.$identifier)*)?\$"

event=${EVENT_NAME:?EVENT_NAME is required}
ref_type=${REF_TYPE:?REF_TYPE is required}
ref_name=${REF_NAME:?REF_NAME is required}
owner=${REPOSITORY_OWNER:?REPOSITORY_OWNER is required}
main_ref=${MAIN_REF:-origin/main}
changelog=${CHANGELOG:-CHANGELOG.md}

case "$event" in
  push)
    [ "$ref_type" = tag ] || die "a push of '$ref_name' ($ref_type) cannot start a release; only a version tag can"
    dry_run=false
    ;;
  workflow_dispatch)
    case "${DRY_RUN:-true}" in
      true) dry_run=true ;;
      false) dry_run=false ;;
      *) die "dry-run must be 'true' or 'false', got '${DRY_RUN}'" ;;
    esac
    if [ "$dry_run" = false ] && [ "$ref_type" != tag ]; then
      die "a real release must be started on a version tag (Use workflow from: the tag), not on the $ref_type '$ref_name'"
    fi
    ;;
  *)
    die "the event '$event' cannot start a release"
    ;;
esac

if [ "$ref_type" = tag ]; then
  tag=$ref_name
  case "$tag" in
    v*) version=${tag#v} ;;
    *) die "the tag '$tag' does not start with 'v'" ;;
  esac
  [[ "$version" =~ $version_pattern ]] \
    || die "the tag '$tag' is not vMAJOR.MINOR.PATCH[-PRERELEASE] (SemVer, no leading zeros, no build metadata)"
  sha=${SHA:?SHA is required for a tag}
  tagged=$(git rev-parse --verify --quiet "refs/tags/$tag^{commit}") || die "the tag '$tag' does not exist in the checkout"
  [ "$tagged" = "$sha" ] || die "the tag '$tag' points to $tagged, but the run is for $sha"
  git rev-parse --verify --quiet "$main_ref^{commit}" > /dev/null || die "'$main_ref' is not available in the checkout"
  git merge-base --is-ancestor "$sha" "$main_ref" || die "the tagged commit $sha is not reachable from $main_ref"
else
  [ "$dry_run" = true ] || die "internal error: a branch run must be a dry run"
  [[ "${RUN_NUMBER:-}" =~ ^[0-9]+$ ]] || die "RUN_NUMBER must be a number for a dry run on a branch"
  tag=
  version="0.0.0-dryrun.$RUN_NUMBER"
fi

if [[ "$version" == *-* ]]; then
  prerelease=true
else
  prerelease=false
fi

[ -f "$changelog" ] || die "the changelog '$changelog' does not exist"
if grep -q "^## \[${version//./\\.}\]" "$changelog"; then
  notes_section=$version
elif [ "$prerelease" = true ] && grep -q '^## \[Unreleased\]' "$changelog"; then
  notes_section=Unreleased
else
  die "the changelog has no '## [$version]' section: move the Unreleased entries under it before tagging"
fi

image="ghcr.io/$(printf '%s' "$owner" | tr '[:upper:]' '[:lower:]')/elpaqueto"

if [ "$prerelease" = true ]; then
  tags=$version
else
  IFS=. read -r major minor _ <<< "$version"
  tags="$version $major.$minor $major latest"
fi

output() {
  printf 'dry_run=%s\n' "$dry_run"
  printf 'tag=%s\n' "$tag"
  printf 'version=%s\n' "$version"
  printf 'prerelease=%s\n' "$prerelease"
  printf 'image=%s\n' "$image"
  printf 'tags=%s\n' "$tags"
  printf 'notes_section=%s\n' "$notes_section"
}

output
if [ -n "${GITHUB_OUTPUT:-}" ]; then
  output >> "$GITHUB_OUTPUT"
fi
