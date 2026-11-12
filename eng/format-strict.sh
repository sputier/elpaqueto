#!/usr/bin/env bash
# Verifies formatting of the strict ElPaqueto projects only (see eng/ElPaqueto.Build.props).
# Usage: eng/format-strict.sh            # check, exits 1 on differences
#        eng/format-strict.sh --fix      # apply formatting
set -euo pipefail
cd "$(dirname "$0")/.."

mode="--verify-no-changes"
[ "${1:-}" = "--fix" ] && mode=""

list=$(dotnet msbuild eng/ElPaqueto.Build.props -getProperty:ElPaquetoStrictProjects | tr -d '[:space:]')
dirs=()
IFS=';' read -ra names <<< "$list"
for name in "${names[@]}"; do
  [ -z "$name" ] && continue
  csproj=$(git ls-files "*/$name/$name.csproj" | head -n 1)
  [ -n "$csproj" ] && dirs+=("$(dirname "$csproj")")
done

if [ "${#dirs[@]}" -eq 0 ]; then
  echo "No strict project exists yet: nothing to format-check."
  exit 0
fi

echo "Checking formatting of: ${dirs[*]}"
dotnet format ElPaqueto.sln $mode --no-restore --include "${dirs[@]}"
