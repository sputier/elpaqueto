#!/usr/bin/env bash
# Creates the buildx builder of the release workflow: the docker-container driver (needed for the
# multi-platform build and its attestations) running a pinned BuildKit image. Prints the builder name.
#
# Usage: eng/release/create-builder.sh [<name>]
#
# The builder uses the host network so that a rehearsal can push to a registry on 127.0.0.1.
# The BuildKit image is pinned by digest, like the SBOM generator image in eng/release/build-image.sh.
# Update the tag and the digest together.
set -euo pipefail

name=${1:-elpaqueto-release}
# moby/buildkit v0.34.2
buildkit_image=moby/buildkit:v0.34.2@sha256:c5f8862f52499773af85bc07695b2379ef08e8002352b0df2c4864c91d78dabe

if ! docker buildx inspect "$name" > /dev/null 2>&1; then
  docker buildx create --name "$name" --driver docker-container \
    --driver-opt "image=$buildkit_image" --driver-opt network=host > /dev/null
fi
docker buildx inspect --bootstrap "$name" > /dev/null
printf '%s\n' "$name"
