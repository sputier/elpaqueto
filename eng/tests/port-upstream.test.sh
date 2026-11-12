#!/usr/bin/env bash
#
# Tests eng/port-upstream.sh on the fixture patch in eng/tests/fixtures/.
#
# Usage: bash eng/tests/port-upstream.test.sh
#
# Fixtures:
#   upstream.patch           upstream-style commit exported with git format-patch
#   upstream.expected.patch  expected script output, compared byte for byte
#   upstream-base/           upstream files before the commit (pre-image blobs);
#                            BaGetter.csproj uses CRLF, like upstream
#                            Directory.Packages.props and Dockerfile
#   elpaqueto-base/          ElPaqueto side of the changed C# and project files,
#                            with LF line endings; Startup.cs has a local edit in
#                            the hunk context that forces the three-way fallback
#                            of git am -3
#   elpaqueto-result/        expected files after git am -3
#
# The fixtures are marked -text in .gitattributes so that their bytes, CRLF
# included, do not depend on core.autocrlf.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
script="$here/../port-upstream.sh"
fixtures="$here/fixtures"

# Isolate git from the user and system configuration (autocrlf, trailers...).
export GIT_CONFIG_NOSYSTEM=1
export GIT_CONFIG_GLOBAL=/dev/null
unset GIT_DIR GIT_WORK_TREE

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

failures=0
pass() { echo "ok   - $1"; }
fail() { echo "FAIL - $1"; failures=$((failures + 1)); }

commit_all() {
  git add -A
  git -c user.name=test -c user.email=test@example.com commit -q -m "$1"
}

# A repository that holds the upstream pre-image blobs, as a real clone with
# the upstream remote fetched does.
repo="$work/repo"
git init -q "$repo"
cp -R "$fixtures/upstream-base/." "$repo/"
(cd "$repo" && commit_all "upstream base")

# 1. Byte-exact mapping.
mkdir "$work/out"
if (cd "$repo" && bash "$script" -o "$work/out" "$fixtures/upstream.patch") 2>"$work/stderr"; then
  pass "script exits with 0"
else
  fail "script exits with 0"
  cat "$work/stderr"
fi
if cmp -s "$fixtures/upstream.expected.patch" "$work/out/upstream.patch"; then
  pass "output matches upstream.expected.patch byte for byte"
else
  fail "output matches upstream.expected.patch byte for byte"
  diff "$fixtures/upstream.expected.patch" "$work/out/upstream.patch" || true
fi

# 2. Running the script again on its own output changes nothing.
cp "$work/out/upstream.patch" "$work/again.patch"
(cd "$repo" && bash "$script" "$work/again.patch") 2>/dev/null
if cmp -s "$fixtures/upstream.expected.patch" "$work/again.patch"; then
  pass "second run in place is a no-op"
else
  fail "second run in place is a no-op"
fi

# 3. The mapped patch applies with git am -3 on the ElPaqueto side, through the
#    three-way fallback, and keeps the author and the trailer.
(
  cd "$repo"
  git switch -q --orphan elpaqueto
  git rm -rq --cached --ignore-unmatch .
  rm -rf src docs
  cp -R "$fixtures/elpaqueto-base/." .
  mkdir -p docs/docs src/ElPaqueto.Core
  cp "$fixtures/upstream-base/docs/docs/configuration.md" docs/docs/
  cp "$fixtures/upstream-base/src/BaGetter.Core/readme.md" src/ElPaqueto.Core/
  commit_all "elpaqueto base"
) >/dev/null
if (cd "$repo" && git -c user.name=test -c user.email=test@example.com am -3 "$work/out/upstream.patch") >"$work/am.log" 2>&1; then
  pass "git am -3 applies the mapped patch"
else
  fail "git am -3 applies the mapped patch"
  cat "$work/am.log"
  (cd "$repo" && git -c user.name=test -c user.email=test@example.com am --abort) || true
fi
if grep -q "Falling back to patching base and 3-way merge" "$work/am.log"; then
  pass "git am used the three-way fallback with the mapped pre-image blob"
else
  fail "git am used the three-way fallback with the mapped pre-image blob"
fi
for file in src/ElPaqueto/Startup.cs src/ElPaqueto/ElPaqueto.csproj; do
  if cmp -s "$fixtures/elpaqueto-result/$file" "$repo/$file"; then
    pass "$file matches elpaqueto-result"
  else
    fail "$file matches elpaqueto-result"
  fi
done
if [ -f "$repo/src/ElPaqueto.Database.Sqlite/Migrations/20260501100000_AddElPaquetoFlag.Designer.cs" ]; then
  pass "added file lands on the mapped path"
else
  fail "added file lands on the mapped path"
fi
author="$(cd "$repo" && git log -1 --format='%an <%ae>')"
if [ "$author" = "Upstream Author <author@example.com>" ]; then
  pass "upstream author is kept"
else
  fail "upstream author is kept (got: $author)"
fi
trailer="$(cd "$repo" && git log -1 --format='%(trailers:key=Upstream-Commit,valueonly)')"
if [ "$trailer" = "947fec777e170647b0a3a7cbf2cb555aa34ca6bb" ]; then
  pass "commit carries the Upstream-Commit trailer"
else
  fail "commit carries the Upstream-Commit trailer (got: $trailer)"
fi

# 4. A file that is not a format-patch output is rejected.
printf 'not a patch\n' >"$work/bad.patch"
if (cd "$repo" && bash "$script" "$work/bad.patch") 2>/dev/null; then
  fail "a non format-patch file is rejected"
else
  pass "a non format-patch file is rejected"
fi

if [ "$failures" -ne 0 ]; then
  echo "$failures check(s) failed"
  exit 1
fi
echo "all checks passed"
