#!/usr/bin/env bash
# Checks that the paths the Docker workflow ignores cannot change the image. .github/workflows/docker.yml
# skips pushes and pull requests that change only docs/** or the Markdown files at the root; that is
# right only while the build never reads them. Fails if the Dockerfile copies or adds anything from
# docs/ or a root Markdown file, or if the workflow ignores paths other than those two.
#
# Usage: bash eng/docker/check-ignored-paths.sh [Dockerfile] [workflow]
set -euo pipefail
cd "$(dirname "$0")/../.."

dockerfile=${1:-Dockerfile}
workflow=${2:-.github/workflows/docker.yml}
failures=0
fail() { printf 'FAIL - %s\n' "$*"; failures=$((failures + 1)); }

# Sources of every COPY/ADD that reads the build context (COPY --from=<stage> reads a stage).
while IFS= read -r line; do
  read -ra words <<< "$line"
  sources=()
  for word in "${words[@]:1}"; do
    case "$word" in --from=*) sources=(); continue 2 ;; --*) ;; *) sources+=("$word") ;; esac
  done
  unset 'sources[${#sources[@]}-1]' # the last word is the destination
  for source in "${sources[@]}"; do
    source=${source#./}
    case "$source" in
      docs | docs/* | . | ./ | \*) fail "the Dockerfile reads '$source', which includes docs/: $line" ;;
      */*) ;;
      *.md | \*.md) fail "the Dockerfile reads the root Markdown file '$source': $line" ;;
    esac
  done
done < <(grep -E '^[[:space:]]*(COPY|ADD)[[:space:]]' "$dockerfile")

expected="'docs/**', '*.md'"
mapfile -t ignored < <(grep -E '^[[:space:]]*paths-ignore:' "$workflow" | sed -E 's/^[[:space:]]*paths-ignore:[[:space:]]*\[(.*)\][[:space:]]*$/\1/')
if [ "${#ignored[@]}" -eq 0 ]; then
  fail "$workflow ignores no path"
fi
for list in "${ignored[@]}"; do
  [ "$list" = "$expected" ] || fail "$workflow ignores [$list]; only [$expected] is known to leave the image unchanged"
done

if [ "$failures" -ne 0 ]; then
  exit 1
fi
echo "ok: the Docker workflow ignores only paths that the image build never reads"
