#!/usr/bin/env bash
# Inspects a pushed multi-platform release image: checks that the index has a linux/amd64 and a
# linux/arm64 image, each with its BuildKit SBOM and provenance attestations, and exports what the
# GitHub attestations need. Used by .github/workflows/release.yml; runs locally the same way.
#
# Usage: eng/release/inspect-image.sh <image> <index-digest> <output-dir>
#
# Writes to <output-dir>:
#   platforms.txt                 "<os>/<arch> <manifest digest>" per platform
#   sbom-<os>-<arch>.spdx.json    the SPDX SBOM that BuildKit attached to each platform image
set -euo pipefail

die() { printf 'inspect-image: %s\n' "$*" >&2; exit 1; }

image=${1:?usage: inspect-image.sh <image> <index-digest> <output-dir>}
digest=${2:?usage: inspect-image.sh <image> <index-digest> <output-dir>}
out=${3:?usage: inspect-image.sh <image> <index-digest> <output-dir>}
[[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || die "malformed digest '$digest'"
ref="$image@$digest"
mkdir -p "$out"

inspect() { docker buildx imagetools inspect "$ref" --format "$1"; }

# Attestation manifests are listed with the platform unknown/unknown; they are not platform images.
inspect '{{range .Manifest.Manifests}}{{.Platform.OS}}/{{.Platform.Architecture}} {{.Digest}}{{"\n"}}{{end}}' \
  | grep -v '^unknown/unknown ' | grep -v '^$' > "$out/platforms.txt" || true
cat "$out/platforms.txt"

fail=0
for platform in linux/amd64 linux/arm64; do
  if ! grep -q "^$platform sha256:[0-9a-f]\{64\}\$" "$out/platforms.txt"; then
    echo "FAIL: no $platform image in $ref"
    fail=1
    continue
  fi
  sbom="$out/sbom-${platform//\//-}.spdx.json"
  inspect "{{json (index .SBOM \"$platform\").SPDX}}" > "$sbom"
  if grep -q '"spdxVersion"' "$sbom"; then
    echo "ok: $platform SBOM ($(wc -c < "$sbom") bytes)"
  else
    echo "FAIL: no SPDX SBOM attached to the $platform image"
    fail=1
  fi
  if inspect "{{json (index .Provenance \"$platform\").SLSA}}" | grep -q '"buildType"'; then
    echo "ok: $platform provenance"
  else
    echo "FAIL: no provenance attached to the $platform image"
    fail=1
  fi
done
[ "$(wc -l < "$out/platforms.txt")" -eq 2 ] || { echo "FAIL: expected exactly two platform images"; fail=1; }
exit "$fail"
