#!/usr/bin/env bash
# Verifies formatting of the strict ElPaqueto projects only (see eng/ElPaqueto.Build.props).
# Usage: eng/format-strict.sh            # check, exits non-zero on differences
#        eng/format-strict.sh --fix      # apply formatting
# Each project is formatted on its own: "dotnet format <solution> --include" does not filter
# reliably, which would turn this check into a silent no-op.
set -euo pipefail
cd "$(dirname "$0")/.."

mode=(--verify-no-changes)
[ "${1:-}" = "--fix" ] && mode=()

list=$(dotnet msbuild eng/ElPaqueto.Build.props -getProperty:ElPaquetoStrictProjects | tr -d '[:space:]')
list=${list#*:\"}   # {"Properties":{"ElPaquetoStrictProjects":";A;B;"}} -> ;A;B;"}}
list=${list%%\"*}

projects=()
IFS=';' read -ra names <<< "$list"
for name in "${names[@]}"; do
  [ -z "$name" ] && continue
  csproj=$(find src tests -maxdepth 2 -name "$name.csproj" -not -path "*/bin/*" -not -path "*/obj/*" | head -n 1)
  [ -n "$csproj" ] && projects+=("$csproj")
done

if [ "${#projects[@]}" -eq 0 ]; then
  echo "No strict project exists yet: nothing to format-check."
  exit 0
fi

status=0
for project in "${projects[@]}"; do
  echo "Checking formatting of $project"
  dotnet format "$project" "${mode[@]}" --no-restore || status=$?
done
exit $status
