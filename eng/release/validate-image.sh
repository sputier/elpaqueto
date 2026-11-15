#!/usr/bin/env bash
# Validates a locally loaded release image before anything is pushed: version and labels
# (check-image.sh), the container smoke test (eng/docker/smoke-test.sh) and the vulnerability scan
# (eng/docker/scan-image.sh). Stops at the first failure. Used by .github/workflows/release.yml.
#
# Usage: eng/release/validate-image.sh <image> <version> <revision> <source-url>
#
# Environment: those of smoke-test.sh (SMOKE_LOG_DIR...) and scan-image.sh (PYTHON); WORK_DIR receives
# the saved image for the scan (default: a temporary folder, removed on exit).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)

image=${1:?usage: validate-image.sh <image> <version> <revision> <source-url>}
version=${2:?usage: validate-image.sh <image> <version> <revision> <source-url>}
revision=${3:?usage: validate-image.sh <image> <version> <revision> <source-url>}
source_url=${4:?usage: validate-image.sh <image> <version> <revision> <source-url>}

work=${WORK_DIR:-}
if [ -z "$work" ]; then
  work=$(mktemp -d)
  trap 'rm -rf "$work"' EXIT
fi

echo "==> version and labels"
bash "$here/check-image.sh" "$image" "$version" "$revision" "$source_url"

echo "==> smoke test"
bash "$here/../docker/smoke-test.sh" "$image"

echo "==> vulnerability scan"
bash "$here/../docker/scan-image.sh" --prepare
docker save "$image" -o "$work/image.tar"
bash "$here/../docker/scan-image.sh" "$work/image.tar"
