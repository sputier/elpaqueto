#!/usr/bin/env bash
# Decides what a run of the development image workflow builds and publishes, and refuses anything else.
# Used by the "verify" job of .github/workflows/publish-development-image.yml; tested by
# eng/tests/development-image.test.sh.
#
# A development image is a build of develop or main, published on demand so that a server can run
# the latest validated code before a release. It is not a release: no version, no GitHub Release,
# never the "latest" tag, and the tags it moves are reserved for it.
#
# Environment (the workflow passes the github context):
#   EVENT_NAME        must be workflow_dispatch: a development image is never published by a push
#   REF_TYPE          must be branch
#   REF_NAME          develop or main
#   RUN_NUMBER        run number, part of the version
#   SHA               full commit SHA of the run
#   REPOSITORY_OWNER  owner of the repository (the image namespace, lowercased)
#
# Output: key=value lines on standard output, also appended to $GITHUB_OUTPUT when it is set:
#   version  0.0.0-dev.RUN_NUMBER: a SemVer prerelease that sorts below every release
#   image    ghcr.io/<owner>/elpaqueto, the same repository as the releases
#   tags     "<branch> sha-<SHA>": a moving tag per branch, and an immutable one per commit
set -euo pipefail

die() { printf 'development-image: %s\n' "$*" >&2; exit 1; }

event=${EVENT_NAME:?EVENT_NAME is required}
ref_type=${REF_TYPE:?REF_TYPE is required}
ref_name=${REF_NAME:?REF_NAME is required}
owner=${REPOSITORY_OWNER:?REPOSITORY_OWNER is required}
sha=${SHA:?SHA is required}
run_number=${RUN_NUMBER:?RUN_NUMBER is required}

[ "$event" = workflow_dispatch ] \
  || die "the event '$event' cannot publish a development image; start the workflow manually"
[ "$ref_type" = branch ] \
  || die "a development image is built from a branch, not from the $ref_type '$ref_name'; a tag is a release"
case "$ref_name" in
  develop | main) ;;
  *) die "a development image is built from develop or main only, not from '$ref_name'" ;;
esac
[[ "$sha" =~ ^[0-9a-f]{40}$ ]] || die "SHA must be a full commit SHA, got '$sha'"
[[ "$run_number" =~ ^[1-9][0-9]*$ ]] || die "RUN_NUMBER must be a positive number, got '$run_number'"

version="0.0.0-dev.$run_number"
image="ghcr.io/$(printf '%s' "$owner" | tr '[:upper:]' '[:lower:]')/elpaqueto"
tags="$ref_name sha-$sha"

output() {
  printf 'version=%s\n' "$version"
  printf 'image=%s\n' "$image"
  printf 'tags=%s\n' "$tags"
}

output
if [ -n "${GITHUB_OUTPUT:-}" ]; then
  output >> "$GITHUB_OUTPUT"
fi
