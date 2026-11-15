#!/usr/bin/env bash
#
# Tests the release decision and release notes scripts (eng/release/resolve-release.sh and
# eng/release/release-notes.sh) in a throwaway git repository. No network, no Docker.
#
# Usage: bash eng/tests/release.test.sh

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
resolve="$here/../release/resolve-release.sh"
notes="$here/../release/release-notes.sh"

# Isolate git from the user and system configuration.
export GIT_CONFIG_NOSYSTEM=1
export GIT_CONFIG_GLOBAL=/dev/null
unset GIT_DIR GIT_WORK_TREE GITHUB_OUTPUT GITHUB_REPOSITORY

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

failures=0
pass() { echo "ok   - $1"; }
fail() { echo "FAIL - $1"; failures=$((failures + 1)); }

# --- Fixture repository -----------------------------------------------------------------------------
# main: A (v1.2.3, v1.2.4-rc.1, v01.2.3...) -- develop: A -- B (v1.2.5, not on main)
repo="$work/repo"
git init -q -b main "$repo"
cd "$repo"
git config user.name "Test"
git config user.email "test@example.com"
cat > CHANGELOG.md << 'EOF'
# Changelog

## [Unreleased]

### Added

- Something new.

## [1.2.3] - 2026-01-02

### Fixed

- A fix.

## [1.2.4-rc.1] - 2026-01-03

- A candidate.

## [0.9.0] - 2025-12-01

[Unreleased]: https://example.com/compare/v1.2.3...HEAD
[1.2.3]: https://example.com/releases/v1.2.3
EOF
git add CHANGELOG.md
git commit -q -m "release"
on_main=$(git rev-parse HEAD)
# Malformed tags exist in the repository, so that the format check is what refuses them
# ("v1.2.3-rc..1" cannot be created: git refuses ".." in a ref name; "V1.2.3" would collide with
# "v1.2.3" on a case-insensitive file system, and tags without "v" are refused first anyway).
for tag in v1.2.3 v1.2.4-rc.1 v1.2.4-rc.2 v2.0.0 v01.2.3 v1.2 v1.2.3+build.1 v1.2.3- v1.2.3-rc_1; do
  git tag "$tag"
done
git switch -q -c develop
git commit -q --allow-empty -m "later"
off_main=$(git rev-parse HEAD)
git tag v1.2.5
git switch -q main
git update-ref refs/remotes/origin/main main

# run <expected status> <description> <env...>: runs resolve-release.sh with a clean environment.
last_output=
run() {
  local expected=$1 description=$2 status=0
  shift 2
  last_output=$(env -i PATH="$PATH" HOME="$work" GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null \
    REPOSITORY_OWNER=Example-Owner RUN_NUMBER=42 "$@" bash "$resolve" 2>&1) || status=$?
  if [ "$expected" = ok ] && [ "$status" -eq 0 ]; then
    pass "$description"
  elif [ "$expected" = fail ] && [ "$status" -ne 0 ]; then
    pass "$description"
  else
    fail "$description (exit $status): $last_output"
  fi
}
expect_line() {
  if printf '%s\n' "$last_output" | grep -qxF -- "$1"; then
    pass "  $1"
  else
    fail "  expected '$1' in: $last_output"
  fi
}
expect_message() {
  if printf '%s\n' "$last_output" | grep -qF -- "$1"; then
    pass "  message: $1"
  else
    fail "  expected message '$1' in: $last_output"
  fi
}

# --- Stable tag push --------------------------------------------------------------------------------
run ok "stable tag push publishes" EVENT_NAME=push REF_TYPE=tag REF_NAME=v1.2.3 SHA="$on_main"
expect_line "dry_run=false"
expect_line "tag=v1.2.3"
expect_line "version=1.2.3"
expect_line "prerelease=false"
expect_line "image=ghcr.io/example-owner/elpaqueto"
expect_line "tags=1.2.3 1.2 1 latest"
expect_line "notes_section=1.2.3"

# --- Prerelease tags --------------------------------------------------------------------------------
run ok "prerelease with its own changelog section" EVENT_NAME=push REF_TYPE=tag REF_NAME=v1.2.4-rc.1 SHA="$on_main"
expect_line "prerelease=true"
expect_line "tags=1.2.4-rc.1"
expect_line "notes_section=1.2.4-rc.1"
run ok "prerelease without a section uses Unreleased" EVENT_NAME=push REF_TYPE=tag REF_NAME=v1.2.4-rc.2 SHA="$on_main"
expect_line "tags=1.2.4-rc.2"
expect_line "notes_section=Unreleased"

# --- Refused tags -----------------------------------------------------------------------------------
for tag in v01.2.3 v1.2 v1.2.3+build.1 v1.2.3- v1.2.3-rc..1 1.2.3 v1.2.3-rc_1 V1.2.3; do
  run fail "malformed tag '$tag' is refused" EVENT_NAME=push REF_TYPE=tag REF_NAME="$tag" SHA="$on_main"
  case "$tag" in
    v*) expect_message "is not vMAJOR.MINOR.PATCH" ;;
    *) expect_message "does not start with 'v'" ;;
  esac
done
run fail "stable release without a changelog section is refused" EVENT_NAME=push REF_TYPE=tag REF_NAME=v2.0.0 SHA="$on_main"
expect_message "no '## [2.0.0]' section"
run fail "tag not reachable from main is refused" EVENT_NAME=push REF_TYPE=tag REF_NAME=v1.2.5 SHA="$off_main"
expect_message "not reachable from origin/main"
run fail "tag pointing elsewhere than the run commit is refused" EVENT_NAME=push REF_TYPE=tag REF_NAME=v1.2.3 SHA="$off_main"
expect_message "points to"
run fail "missing tag is refused" EVENT_NAME=push REF_TYPE=tag REF_NAME=v9.9.9 SHA="$on_main"
run fail "missing main ref is refused" EVENT_NAME=push REF_TYPE=tag REF_NAME=v1.2.3 SHA="$on_main" MAIN_REF=origin/nope

# --- Events and dry runs ----------------------------------------------------------------------------
run fail "branch push is refused" EVENT_NAME=push REF_TYPE=branch REF_NAME=develop SHA="$on_main"
for event in pull_request pull_request_target workflow_run schedule; do
  run fail "event '$event' is refused" EVENT_NAME="$event" REF_TYPE=tag REF_NAME=v1.2.3 SHA="$on_main"
done
run ok "manual run defaults to a dry run" EVENT_NAME=workflow_dispatch REF_TYPE=branch REF_NAME=develop SHA="$on_main"
expect_line "dry_run=true"
expect_line "tag="
expect_line "version=0.0.0-dryrun.42"
expect_line "prerelease=true"
expect_line "tags=0.0.0-dryrun.42"
expect_line "notes_section=Unreleased"
run ok "explicit dry run on a branch" EVENT_NAME=workflow_dispatch DRY_RUN=true REF_TYPE=branch REF_NAME=develop SHA="$on_main"
expect_line "dry_run=true"
run fail "real manual release on a branch is refused" EVENT_NAME=workflow_dispatch DRY_RUN=false REF_TYPE=branch REF_NAME=develop SHA="$on_main"
expect_message "must be started on a version tag"
run fail "invalid dry-run value is refused" EVENT_NAME=workflow_dispatch DRY_RUN=yes REF_TYPE=branch REF_NAME=develop SHA="$on_main"
run ok "dry run on a tag checks the tag" EVENT_NAME=workflow_dispatch REF_TYPE=tag REF_NAME=v1.2.3 SHA="$on_main"
expect_line "dry_run=true"
expect_line "tags=1.2.3 1.2 1 latest"
run fail "dry run on an off-main tag still fails" EVENT_NAME=workflow_dispatch REF_TYPE=tag REF_NAME=v1.2.5 SHA="$off_main"
run ok "real manual release on a tag" EVENT_NAME=workflow_dispatch DRY_RUN=false REF_TYPE=tag REF_NAME=v1.2.3 SHA="$on_main"
expect_line "dry_run=false"
run fail "dry run on a branch needs a numeric run number" EVENT_NAME=workflow_dispatch REF_TYPE=branch REF_NAME=develop SHA="$on_main" RUN_NUMBER='1;x'

# --- GITHUB_OUTPUT ----------------------------------------------------------------------------------
output_file="$work/github-output"
: > "$output_file"
run ok "outputs are appended to GITHUB_OUTPUT" EVENT_NAME=push REF_TYPE=tag REF_NAME=v1.2.3 SHA="$on_main" GITHUB_OUTPUT="$output_file"
if grep -qx "tags=1.2.3 1.2 1 latest" "$output_file" && [ "$(wc -l < "$output_file")" -eq 7 ]; then
  pass "  GITHUB_OUTPUT has the seven outputs"
else
  fail "  GITHUB_OUTPUT content: $(cat "$output_file")"
fi

# --- Release notes ----------------------------------------------------------------------------------
digest="sha256:$(printf '%064d' 7)"
if out=$(bash "$notes" CHANGELOG.md 1.2.3 ghcr.io/example-owner/elpaqueto "$digest"); then
  expected_start=$'### Fixed\n\n- A fix.\n\n## Container image'
  if [[ "$out" == "$expected_start"* ]] \
    && printf '%s\n' "$out" | grep -qxF "docker pull ghcr.io/example-owner/elpaqueto@$digest" \
    && ! printf '%s\n' "$out" | grep -qF "A candidate"; then
    pass "release notes hold the section and the pull-by-digest line"
  else
    fail "release notes content: $out"
  fi
else
  fail "release notes for 1.2.3"
fi
if out=$(bash "$notes" CHANGELOG.md Unreleased); then
  if [ "$out" = $'### Added\n\n- Something new.' ]; then
    pass "Unreleased notes without an image"
  else
    fail "Unreleased notes content: $out"
  fi
else
  fail "Unreleased notes"
fi
if out=$(bash "$notes" CHANGELOG.md 1.2.4-rc.1) && [ "$out" = "- A candidate." ]; then
  pass "section followed by another section"
else
  fail "section followed by another section: $out"
fi
if bash "$notes" CHANGELOG.md 0.9.0 > /dev/null 2>&1; then
  fail "empty section must fail"
else
  pass "empty section fails"
fi
if bash "$notes" CHANGELOG.md 3.0.0 > /dev/null 2>&1; then
  fail "missing section must fail"
else
  pass "missing section fails"
fi
if bash "$notes" CHANGELOG.md 1.2 > /dev/null 2>&1; then
  fail "a version prefix must not match a longer section"
else
  pass "a version prefix does not match a longer section"
fi
if bash "$notes" CHANGELOG.md 1.2.3 ghcr.io/example-owner/elpaqueto sha256:abc > /dev/null 2>&1; then
  fail "malformed digest must fail"
else
  pass "malformed digest fails"
fi
if bash "$notes" CHANGELOG.md 1.2.3 ghcr.io/example-owner/elpaqueto > /dev/null 2>&1; then
  fail "image without digest must fail"
else
  pass "image without digest fails"
fi

# --- The repository's own changelog -----------------------------------------------------------------
if bash "$notes" "$here/../../CHANGELOG.md" Unreleased > /dev/null; then
  pass "CHANGELOG.md has a non-empty Unreleased section"
else
  fail "CHANGELOG.md has no usable Unreleased section"
fi

echo
if [ "$failures" -ne 0 ]; then
  echo "$failures failure(s)"
  exit 1
fi
echo "all release tests passed"
