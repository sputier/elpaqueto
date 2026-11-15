#!/usr/bin/env bash
#
# Maps upstream BaGetter patches to ElPaqueto names before `git am -3`.
#
# Usage: eng/port-upstream.sh [-o <output-dir>] <patch>...
#
# Each <patch> is one commit exported with `git format-patch` (one file per
# commit, not --stdout). Without -o, the patches are rewritten in place.
#
# The script applies the rename rules of #58 (docs/adr/0001) to the paths and
# contents of the diff, keeps the commit message and author unchanged, and
# appends an `Upstream-Commit: <full sha>` trailer to the commit message.
# When run inside a repository that contains the upstream blobs, it also writes
# mapped copies of the pre-image and post-image blobs and rewrites the `index`
# lines to point at them, so that the three-way fallback of `git am -3` works.
#
# Procedure and rules: docs/upstream-porting.md.
# Requirements: bash 4 or later, GNU sed, git. On Windows, run it with the Git
# Bash shipped with Git for Windows. Every sed call uses -b so that sed gives
# the same bytes on Windows as on Linux (without it, Windows sed drops CRs).

set -euo pipefail

usage() {
  echo "Usage: $0 [-o <output-dir>] <patch>..." >&2
  exit 2
}

# Content rules, applied to every line of a mapped file section (diff headers
# and hunk lines) and to the mapped blobs. The first rule turns CRLF into LF:
# ElPaqueto stores every text file with LF, while a few upstream files use CRLF.
# Lines matching one of the next three addresses are then kept unchanged:
# upstream URLs, the Azure Search analyzer name stored in existing indexes, and
# EF Core migration IDs.
readonly MAP_SED='
s/\r$//
/[Hh][Tt][Tt][Pp][Ss]?:\/\/[^[:space:]]*([Bb][Aa][Gg][Ee][Tt]|loic-sharma)/b
/baget-exact-match-analyzer/b
/\[Migration\("/b
s/BaGetter/ElPaqueto/g
s/BaGet/ElPaqueto/g
s/bagetter/elpaqueto/g
s/BAGET_CONFIG_ROOT/ELPAQUETO_CONFIG_ROOT/g
s/baGetter/elPaqueto/g
s/\bbaget\b/elpaqueto/g
'

# Same rules, restricted to the diff header of a file section: everything
# before the first hunk or binary patch is mapped, the rest is left unchanged.
readonly HEADER_SED='
/^(@@ |GIT binary patch|Binary files )/,$b
'"$MAP_SED"

out_dir=""
while getopts ":o:h" opt; do
  case "$opt" in
    o) out_dir="$OPTARG" ;;
    *) usage ;;
  esac
done
shift $((OPTIND - 1))
[ $# -ge 1 ] || usage

if [ -n "$out_dir" ] && [ ! -d "$out_dir" ]; then
  echo "error: output directory '$out_dir' does not exist" >&2
  exit 2
fi

use_blobs=""
if git rev-parse --git-dir >/dev/null 2>&1; then
  use_blobs=1
fi

# Prints the full ID of a mapped copy of blob $1, or $1 itself when the blob is
# unknown (not fetched) or is the null ID of an added or deleted file.
map_blob() {
  local id="$1" full mapped
  if [ -z "$use_blobs" ] || [ -z "${id//0/}" ]; then
    printf '%s' "$id"
    return
  fi
  if ! full=$(git rev-parse -q --verify "$id^{blob}" 2>/dev/null); then
    printf '%s' "$id"
    return
  fi
  mapped=$(git cat-file blob "$full" | sed -b -E "$MAP_SED" | git hash-object -w --stdin)
  if [ "$mapped" = "$full" ]; then
    printf '%s' "$id"
  else
    printf '%s' "$mapped"
  fi
}

# Maps one file section (from its `diff --git` line to the next one) read from
# stdin, according to its path. Reports the decision on stderr.
map_section() {
  local section first index old new old_mapped new_mapped
  section=$(cat; printf x)
  section="${section%x}"
  first="${section%%$'\n'*}"

  if [[ "$first" =~ ^diff\ --git\ a/(docs|deployment\ templates)/ ]]; then
    echo "  unchanged    ${first#diff --git }" >&2
    printf '%s' "$section"
    return
  fi

  if [[ "$first" =~ \.[mM][dD]$ ]] || [[ "$section" == *$'\nGIT binary patch\n'* ]]; then
    echo "  path only    ${first#diff --git }" >&2
    printf '%s' "$section" | sed -b -E "$HEADER_SED"
    return
  fi

  echo "  mapped       ${first#diff --git }" >&2
  index=$(printf '%s' "$section" | sed -b -n -E '/^(@@ |GIT binary patch|Binary files )/q; /^index [0-9a-f]+\.\.[0-9a-f]+/{p;q}')
  if [ -n "$index" ]; then
    old="${index#index }"; old="${old%%..*}"
    new="${index#*..}"; new="${new%% *}"
    old_mapped=$(map_blob "$old")
    new_mapped=$(map_blob "$new")
    printf '%s' "$section" | sed -b -E "$MAP_SED" \
      | sed -b -E "0,/^index $old\.\.$new/s//index $old_mapped..$new_mapped/"
  else
    printf '%s' "$section" | sed -b -E "$MAP_SED"
  fi
}

tmp=""
trap 'rm -f "$tmp"' EXIT

map_patch() {
  local patch="$1" target="$2" sha starts count i start end
  sha=$(sed -b -n -E '1s/^From ([0-9a-f]{40}) .*/\1/p' "$patch")
  if [ -z "$sha" ]; then
    echo "error: $patch does not start with 'From <sha>': not a git format-patch file" >&2
    return 1
  fi
  count=$(sed -b -n -E '/^From [0-9a-f]{40} Mon Sep 17 00:00:00 2001$/p' "$patch" | sed -b -n '$=')
  if [ "$count" != 1 ]; then
    echo "error: $patch contains $count commits; export one commit per file" >&2
    return 1
  fi

  echo "$patch (upstream $sha)" >&2
  mapfile -t starts < <(sed -b -n '/^diff --git /=' "$patch")

  tmp="$target.tmp.$$"
  {
    if [ ${#starts[@]} -eq 0 ]; then
      cat "$patch"
    else
      sed -b -n "1,$((starts[0] - 1))p" "$patch"
      for ((i = 0; i < ${#starts[@]}; i++)); do
        start=${starts[i]}
        if [ $((i + 1)) -lt ${#starts[@]} ]; then
          end=$((starts[i + 1] - 1))
        else
          end='$'
        fi
        sed -b -n "${start},${end}p" "$patch" | map_section
      done
    fi
  } >"$tmp"

  git interpret-trailers --in-place --if-exists doNothing \
    --trailer "Upstream-Commit: $sha" "$tmp"
  mv -f "$tmp" "$target"
}

for patch in "$@"; do
  if [ ! -f "$patch" ]; then
    echo "error: $patch not found" >&2
    exit 1
  fi
  if [ -n "$out_dir" ]; then
    map_patch "$patch" "$out_dir/$(basename "$patch")"
  else
    map_patch "$patch" "$patch"
  fi
done
