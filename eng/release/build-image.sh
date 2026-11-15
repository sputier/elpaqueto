#!/usr/bin/env bash
# Builds the release image with the Docker CLI (buildx) only. Used by .github/workflows/release.yml;
# runs locally the same way.
#
#   eng/release/build-image.sh load <name:tag>     linux/amd64 image loaded into the local image store,
#                                                  for the checks, the smoke test and the scan
#   eng/release/build-image.sh push <metadata.json> every platform, with the provenance and SBOM
#                                                  attestations, pushed to IMAGE under each of TAGS;
#                                                  buildx writes its metadata to the file, and the
#                                                  digest of the pushed index is printed as
#                                                  "digest=sha256:..." (also appended to $GITHUB_OUTPUT)
#
# Environment:
#   VERSION     version of the application and of the image label (required)
#   REVISION    full commit SHA (required)
#   SOURCE_URL  repository URL for the source label (required)
#   IMAGE       ghcr.io/<owner>/elpaqueto (push only)
#   TAGS        space-separated image tags (push only)
#   PLATFORMS   default linux/amd64,linux/arm64
#   BUILDER     buildx builder; "push" needs one with the docker-container driver (create-builder.sh)
#
# The push mode needs QEMU for the foreign platform (the SDK stage cross-compiles, only the
# runtime stage's package installation runs emulated) and a docker-container builder for attestations.
set -euo pipefail

die() { printf 'build-image: %s\n' "$*" >&2; exit 1; }

mode=${1:?usage: build-image.sh load <name:tag> | push <metadata.json>}
target=${2:?usage: build-image.sh load <name:tag> | push <metadata.json>}
version=${VERSION:?VERSION is required}
revision=${REVISION:?REVISION is required}
source_url=${SOURCE_URL:?SOURCE_URL is required}
platforms=${PLATFORMS:-linux/amd64,linux/arm64}
case "$target" in
  /*) ;;
  *) [ "$mode" = load ] || target="$PWD/$target" ;;
esac
cd "$(dirname "$0")/../.."

[[ "$revision" =~ ^[0-9a-f]{40}$ ]] || die "REVISION must be a full commit SHA"

args=(buildx build)
if [ -n "${BUILDER:-}" ]; then
  args+=(--builder "$BUILDER")
fi
args+=(
  --build-arg "Version=$version"
  --build-arg "REVISION=$revision"
  --build-arg "SOURCE_URL=$source_url"
)

# Annotations of the multi-platform index: registries show them for the index, where the labels of
# each platform's configuration are not visible.
index_annotations=(
  --annotation "index:org.opencontainers.image.title=ElPaqueto"
  --annotation "index:org.opencontainers.image.description=Self-hosted private NuGet server"
  --annotation "index:org.opencontainers.image.source=$source_url"
  --annotation "index:org.opencontainers.image.url=$source_url"
  --annotation "index:org.opencontainers.image.licenses=MIT"
  --annotation "index:org.opencontainers.image.version=$version"
  --annotation "index:org.opencontainers.image.revision=$revision"
)
# SPDX SBOM generator run by BuildKit, pinned by digest (docker/buildkit-syft-scanner 1.12.1).
sbom_generator=docker/buildkit-syft-scanner:1.12.1@sha256:873a7458c3c8ac8e32c7b796c10e1600b07b963d2aa000a72a2d97aaaa2d6315
attestations=(--provenance=mode=max "--sbom=generator=$sbom_generator")

case "$mode" in
  load)
    args+=(--platform linux/amd64 --provenance=false --sbom=false --tag "$target" --load)
    ;;
  push)
    image=${IMAGE:?IMAGE is required for push}
    tags=${TAGS:?TAGS is required for push}
    # A local registry is accepted for rehearsals on a workstation.
    [[ "$image" =~ ^(ghcr\.io|localhost:[0-9]+)/[a-z0-9-]+/elpaqueto$ ]] \
      || die "IMAGE must be ghcr.io/<owner>/elpaqueto, got '$image'"
    args+=(--platform "$platforms" "${attestations[@]}" "${index_annotations[@]}"
      --metadata-file "$target" --push)
    for tag in $tags; do
      [[ "$tag" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$ ]] || die "invalid image tag '$tag'"
      args+=(--tag "$image:$tag")
    done
    ;;
  *)
    die "unknown mode '$mode'"
    ;;
esac

args+=(.)
printf 'docker'
printf ' %q' "${args[@]}"
printf '\n'
docker "${args[@]}"

if [ "$mode" = push ]; then
  digest=$("${PYTHON:-python3}" -c 'import json, sys; print(json.load(open(sys.argv[1]))["containerimage.digest"])' "$target")
  [[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || die "no valid image digest in $target: '$digest'"
  printf 'digest=%s\n' "$digest"
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    printf 'digest=%s\n' "$digest" >> "$GITHUB_OUTPUT"
  fi
fi
