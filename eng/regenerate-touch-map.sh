#!/usr/bin/env bash
#
# Regenerates the raw data of docs/technical/upstream-touch-map.md.
#
# Usage: regenerate-touch-map.sh [-b <upstream-baseline>] [-r <rev>] <output-dir>
#
#   -b  upstream baseline commit (default: 02b6706, see docs/upstream-porting.md)
#   -r  ElPaqueto revision to compare (default: HEAD)
#
# Run it from the root of an ElPaqueto clone that has the `upstream` remote
# fetched (the baseline commit must be present). Nothing in the clone changes:
# the work happens in a temporary shared clone, and only <output-dir> is written.
#
# Steps:
#   1. Build a commit whose tree is the upstream baseline tree and whose parent
#      has the empty tree, so that `git format-patch` exports the whole
#      upstream tree as one patch of added files.
#   2. Map that patch with eng/port-upstream.sh, exactly as a port does.
#   3. Apply the mapped patch to an empty index: the result is the "mapped
#      upstream baseline" tree.
#   4. Compare that tree with <rev> (CR at end of line ignored, no rename
#      detection) and list, for each modified or deleted inherited file, the
#      ElPaqueto commits that touched it since the baseline.
#
# Requirements: bash 4 or later, GNU sed, git (Git Bash on Windows).

set -euo pipefail

baseline=02b6706
rev=HEAD
while getopts ":b:r:h" opt; do
  case "$opt" in
    b) baseline="$OPTARG" ;;
    r) rev="$OPTARG" ;;
    *) echo "Usage: $0 [-b <upstream-baseline>] [-r <rev>] <output-dir>" >&2; exit 2 ;;
  esac
done
shift $((OPTIND - 1))
[ $# -eq 1 ] || { echo "Usage: $0 [-b <upstream-baseline>] [-r <rev>] <output-dir>" >&2; exit 2; }

repo=$(git rev-parse --show-toplevel)
mkdir -p "$1"
out=$(cd "$1" && pwd)
base_full=$(git -C "$repo" rev-parse --verify "$baseline^{commit}")
rev_full=$(git -C "$repo" rev-parse --verify "$rev^{commit}")

work=$(mktemp -d)
cleanup() { [ -n "$work" ] && [ -d "$work" ] && rm -rf -- "$work"; }
trap cleanup EXIT

# Shared clone: reads the objects of $repo, writes the mapped blobs to its own store.
git clone -q --shared --no-checkout "$repo" "$work/clone"
cd "$work/clone"
export GIT_AUTHOR_NAME=touch-map GIT_AUTHOR_EMAIL=touch-map@example.invalid
export GIT_COMMITTER_NAME=touch-map GIT_COMMITTER_EMAIL=touch-map@example.invalid

# 1. Whole upstream tree as one patch.
empty_tree=$(git hash-object -t tree -w --stdin </dev/null)
empty_commit=$(git commit-tree "$empty_tree" -m "empty tree")
base_commit=$(git commit-tree "$base_full^{tree}" -p "$empty_commit" -m "upstream $base_full tree")
mkdir "$work/patch" "$work/mapped"
git format-patch -q -1 --binary --no-renames -o "$work/patch" "$base_commit"

# 2. Map it. Large file sections make the index-line extraction of
# eng/port-upstream.sh die of SIGPIPE under `set -o pipefail` (exit 141), so a
# temporary copy tolerates that one pipe; the mapping rules are unchanged.
sed 's/{p;q}'"'"')$/{p;q}'"'"') || true/' "$repo/eng/port-upstream.sh" >"$work/port-upstream.sh"
bash "$work/port-upstream.sh" -o "$work/mapped" "$work/patch"/*.patch 2>"$out/port-upstream.log"

# 3. Mapped upstream baseline tree.
export GIT_INDEX_FILE="$work/mapped.index"
git read-tree --empty
git apply --cached --whitespace=nowarn "$work/mapped"/*.patch
mapped_tree=$(git write-tree)
unset GIT_INDEX_FILE

# 4. Comparison.
{
  echo "upstream baseline: $base_full"
  echo "ElPaqueto revision: $rev_full ($(git -C "$repo" log -1 --format=%cs "$rev_full"))"
  echo "mapped baseline tree: $mapped_tree"
} >"$out/summary.txt"

git diff --no-renames --name-status "$mapped_tree" "$rev_full" >"$out/name-status.txt"
git diff --no-renames --ignore-cr-at-eol --numstat --diff-filter=M "$mapped_tree" "$rev_full" \
  -- . ':(exclude)docs/' ':(exclude)deployment templates/' >"$out/numstat-modified.txt"
git diff --no-renames --ignore-cr-at-eol --diff-filter=M "$mapped_tree" "$rev_full" \
  -- . ':(exclude)docs/' ':(exclude)deployment templates/' >"$out/diff-modified.patch"

# Per-file history since the baseline (--follow crosses the #58 rename).
while IFS=$'\t' read -r status path; do
  case "$path" in docs/*|"deployment templates/"*) continue ;; esac
  echo "=== $status $path"
  if [ "$status" = M ]; then
    git -C "$repo" log --no-merges --format='%h %s' --follow "$base_full..$rev_full" -- "$path"
  else
    git -C "$repo" log --no-merges --format='%h %s' "$base_full..$rev_full" -- "$path"
  fi
done < <(grep -E $'^[MD]\t' "$out/name-status.txt") >"$out/file-logs.txt"

# Issue references of those commits (commit message, then the merging pull request).
for sha in $(grep -v '^===' "$out/file-logs.txt" | cut -d' ' -f1 | sort -u); do
  refs=$(git -C "$repo" log -1 --format=%B "$sha" | grep -oE '#[0-9]+' | sort -u | tr '\n' ' ' || true)
  merge=$(git -C "$repo" log --merges --ancestry-path --reverse --format=%s "$sha..$rev_full" | head -n 1 || true)
  printf '%s | %s| %s\n' "$sha" "$refs" "$merge"
done >"$out/commit-issues.txt"

echo "modified (outside docs/ and deployment templates/): $(wc -l <"$out/numstat-modified.txt")" >>"$out/summary.txt"
echo "deleted: $(grep -c $'^D\t' "$out/name-status.txt" || true)" >>"$out/summary.txt"
echo "added: $(grep -c $'^A\t' "$out/name-status.txt" || true)" >>"$out/summary.txt"
cat "$out/summary.txt"
