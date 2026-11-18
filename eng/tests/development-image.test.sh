#!/usr/bin/env bash
#
# Tests the development image decision script (eng/release/resolve-development-image.sh), and checks
# that its workflow keeps the guarantees the script cannot enforce alone. No network, no Docker.
#
# Usage: bash eng/tests/development-image.test.sh

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
resolve="$here/../release/resolve-development-image.sh"
workflow="$here/../../.github/workflows/publish-development-image.yml"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

failures=0
pass() { echo "ok   - $1"; }
fail() { echo "FAIL - $1"; failures=$((failures + 1)); }

sha=0123456789abcdef0123456789abcdef01234567

# run <expected status> <description> <env...>: runs the script with a clean environment, defaulting to
# a valid manual run on develop; the arguments override those defaults.
last_output=
run() {
  local expected=$1 description=$2 status=0
  shift 2
  last_output=$(env -i PATH="$PATH" HOME="$work" \
    EVENT_NAME=workflow_dispatch REF_TYPE=branch REF_NAME=develop RUN_NUMBER=42 SHA="$sha" \
    REPOSITORY_OWNER=Example-Owner "$@" bash "$resolve" 2>&1) || status=$?
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

# --- Accepted runs ----------------------------------------------------------------------------------
run ok "manual run on develop publishes a development image"
expect_line "version=0.0.0-dev.42"
expect_line "image=ghcr.io/example-owner/elpaqueto"
expect_line "tags=develop sha-$sha"

run ok "manual run on main publishes a development image" REF_NAME=main
expect_line "tags=main sha-$sha"

run ok "the outputs are appended to GITHUB_OUTPUT" GITHUB_OUTPUT="$work/output"
if grep -qxF "tags=develop sha-$sha" "$work/output" 2>/dev/null; then
  pass "  GITHUB_OUTPUT has the tags"
else
  fail "  GITHUB_OUTPUT does not have the tags"
fi

# --- Refused runs -----------------------------------------------------------------------------------
run fail "a push never publishes a development image" EVENT_NAME=push
expect_message "start the workflow manually"
run fail "a pull request never publishes a development image" EVENT_NAME=pull_request
run fail "a tag is a release, not a development image" REF_TYPE=tag REF_NAME=v1.2.3
expect_message "a tag is a release"
run fail "a feature branch is refused" REF_NAME=feat/something
expect_message "develop or main only"
run fail "a branch named like a release tag is refused" REF_NAME=latest
run fail "an abbreviated SHA is refused" SHA=0123456
run fail "an upper-case SHA is refused" SHA=0123456789ABCDEF0123456789ABCDEF01234567
run fail "a missing run number is refused" RUN_NUMBER=
run fail "a non-numeric run number is refused" RUN_NUMBER=4a
run fail "a run number with a leading zero is refused" RUN_NUMBER=042

# --- Workflow guarantees ----------------------------------------------------------------------------
# The hygiene check refuses a publishing workflow with any trigger but a tag push or a manual run;
# these checks pin what this workflow adds on top of that rule.
if [ -f "$workflow" ]; then
  triggers=$(sed -n '/^on:/,/^[a-z]/p' "$workflow" | grep -E '^  [a-z_]+:' | tr -d ' :' | sort | tr '\n' ' ')
  if [ "$triggers" = "workflow_dispatch " ]; then
    pass "the workflow is started manually only"
  else
    fail "the workflow must have workflow_dispatch as its only trigger, got: $triggers"
  fi
  for called in ci.yml docker.yml; do
    if grep -qE "uses: \./\.github/workflows/$called\$" "$workflow"; then
      pass "the workflow validates the commit with $called"
    else
      fail "the workflow does not call $called"
    fi
  done
  if grep -qE 'needs: \[verify, ci, docker\]' "$workflow"; then
    pass "publishing waits for verify, ci and docker"
  else
    fail "the publish job must need verify, ci and docker"
  fi
  if grep -qE '(^|[^a-z])latest([^a-z]|$)' <(grep -E '^\s*(TAGS|tags):' "$workflow"); then
    fail "the workflow must never set the latest tag"
  else
    pass "the workflow never sets the latest tag"
  fi
else
  fail "the workflow $workflow does not exist"
fi

echo
if [ "$failures" -ne 0 ]; then
  echo "$failures failure(s)"
  exit 1
fi
echo "all development image tests passed"
