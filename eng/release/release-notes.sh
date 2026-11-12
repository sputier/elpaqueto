#!/usr/bin/env bash
# Writes the release notes of a version on standard output: the changelog section, then, when an image
# digest is given, the published image and the commands that pull and verify it by digest.
# Used by .github/workflows/release.yml; tested by eng/tests/release.test.sh.
#
# Usage: eng/release/release-notes.sh <changelog> <section> [<image> <digest>]
#
#   <section>  "Unreleased" or a version, as written in the "## [...]" heading
#   <image>    ghcr.io/<owner>/elpaqueto
#   <digest>   sha256:<64 hex digits>, the digest of the multi-platform image index
#
# Fails when the section is missing or empty, or when the digest is malformed.
set -euo pipefail

die() { printf 'release-notes: %s\n' "$*" >&2; exit 1; }

changelog=${1:?usage: release-notes.sh <changelog> <section> [<image> <digest>]}
section=${2:?usage: release-notes.sh <changelog> <section> [<image> <digest>]}
image=${3:-}
digest=${4:-}

[ -f "$changelog" ] || die "the changelog '$changelog' does not exist"

# The body runs from the section heading to the next "## " heading or to the link definitions.
body=$(awk -v heading="## [$section]" '
  index($0, heading) == 1 { inside = 1; found = 1; next }
  inside && (/^## / || /^\[[^]]+\]: /) { inside = 0 }
  inside { print }
  END { if (!found) exit 2 }
' "$changelog") || die "the changelog has no '## [$section]' section"

# Drop the leading blank lines; the command substitution drops the trailing ones.
body=$(printf '%s\n' "$body" | sed -e '/./,$!d')
[ -n "$body" ] || die "the '## [$section]' section of the changelog is empty"

printf '%s\n' "$body"

if [ -n "$image" ] || [ -n "$digest" ]; then
  [ -n "$image" ] && [ -n "$digest" ] || die "the image and the digest go together"
  [[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || die "malformed digest '$digest'"
  cat << EOF

## Container image

Image \`$image\`, multi-platform (linux/amd64, linux/arm64), digest \`$digest\`.

\`\`\`sh
docker pull $image@$digest
gh attestation verify oci://$image@$digest --repo ${GITHUB_REPOSITORY:-<owner>/<repository>}
\`\`\`
EOF
fi
