#!/usr/bin/env bash
# Checks that a locally loaded release image carries the expected version and revision, both in its
# labels and in the application assemblies (the version given to "dotnet publish -p Version=").
# Used by .github/workflows/release.yml before anything is pushed; runs locally the same way.
#
# Usage: eng/release/check-image.sh <image> <version> <revision> <source-url>
set -euo pipefail
# Git Bash on Windows: pass container paths to docker as they are.
docker() { MSYS_NO_PATHCONV=1 command docker "$@"; }

image=${1:?usage: check-image.sh <image> <version> <revision> <source-url>}
version=${2:?usage: check-image.sh <image> <version> <revision> <source-url>}
revision=${3:?usage: check-image.sh <image> <version> <revision> <source-url>}
source_url=${4:?usage: check-image.sh <image> <version> <revision> <source-url>}

fail=0
check() {
  if [ "$2" = "$3" ]; then
    echo "ok: $1"
  else
    echo "FAIL: $1: expected '$3', got '$2'"
    fail=1
  fi
}
label() { docker image inspect "$image" --format "{{index .Config.Labels \"$1\"}}"; }

check "version label" "$(label org.opencontainers.image.version)" "$version"
check "revision label" "$(label org.opencontainers.image.revision)" "$revision"
check "source label" "$(label org.opencontainers.image.source)" "$source_url"
check "licenses label" "$(label org.opencontainers.image.licenses)" "MIT"

# The assembly's informational version attribute holds exactly the version (the image is built without
# a .git folder, so no source revision is appended). A custom attribute stores a string as a length
# byte followed by the UTF-8 text, so the needle "<length><version>" matches this exact value only, not
# a longer version that contains it.
[ "${#version}" -lt 128 ] || { echo "FAIL: version longer than 127 characters"; exit 1; }
for assembly in ElPaqueto.dll ElPaqueto.Web.dll; do
  # shellcheck disable=SC2016 # expanded by the container shell
  if docker run --rm --entrypoint sh "$image" -c \
    'grep -q -F -- "$(printf "\\$(printf %03o "${#1}")")$1" "/app/$2"' sh "$version" "$assembly"; then
    echo "ok: $assembly built with version $version"
  else
    echo "FAIL: $assembly does not carry the version $version"
    fail=1
  fi
done

exit "$fail"
