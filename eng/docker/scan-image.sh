#!/usr/bin/env bash
# Vulnerability scan of an ElPaqueto image with the pinned Trivy release (see eng/hygiene/install_tools.py:
# version and SHA-256 are recorded there). Used by the Docker workflow; runs locally the same way.
#
#   eng/docker/scan-image.sh --prepare                  install Trivy and download its database (retried)
#   eng/docker/scan-image.sh <image.tar> [options...]   scan an image saved with "docker save"
#
# The scan fails on HIGH and CRITICAL findings that have a fixed version. Accepted findings are listed
# in .trivyignore, each with a justification and an expiry date (policy in CONTRIBUTING.md, format
# checked by eng/hygiene/check_trivyignore.py). Extra options are passed to Trivy (for example
# "--format sarif --output trivy.sarif --exit-code 0").
#
# PYTHON selects the Python interpreter (default python3).
set -euo pipefail
cd "$(dirname "$0")/../.."

trivy=$("${PYTHON:-python3}" eng/hygiene/install_tools.py trivy | tail -n 1)

if [ "${1:-}" = "--prepare" ]; then
  "$trivy" --version
  for attempt in 1 2 3; do
    # The default database repositories are a mirror first, then the GitHub registry.
    if "$trivy" image --download-db-only --no-progress; then
      exit 0
    fi
    echo "vulnerability database download failed (attempt $attempt)" >&2
    sleep $((attempt * 15))
  done
  exit 1
fi

input=${1:?usage: scan-image.sh --prepare | <image.tar> [trivy options...]}
shift
"$trivy" image --input "$input" \
  --skip-db-update --skip-version-check --no-progress \
  --scanners vuln --severity HIGH,CRITICAL --ignore-unfixed \
  --ignorefile .trivyignore \
  --exit-code 1 \
  "$@"
