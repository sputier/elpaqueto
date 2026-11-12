#!/usr/bin/env python3
"""Project layout rules.

Fails when:
- a directory contains more than one project file (``dotnet build`` run from
  that directory then fails);
- a project under ``src/``, ``tests/`` or ``samples/`` is missing from the
  solution, unless it is listed in ``EXCLUDED`` with a reason.
"""

from __future__ import annotations

import re
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from hygiene_common import ConfigError, Violation, repo_root, run_main, tracked_files  # noqa: E402

SOLUTION = "ElPaqueto.sln"
PROJECT_EXTENSIONS = (".csproj", ".fsproj", ".vbproj")
SOLUTION_ROOTS = ("src/", "tests/", "samples/")
SLN_PROJECT = re.compile(r'^Project\("[^"]*"\)\s*=\s*"[^"]*",\s*"([^"]+)"', re.MULTILINE)

# Projects deliberately left out of the solution: path -> reason.
EXCLUDED: dict[str, str] = {
    "src/ElPaqueto.Database.MySql/ElPaqueto.Database.MySql.csproj": (
        "Inherited MySQL provider, neither referenced by the server nor part of "
        "the solution upstream; restoring or removing it is a separate decision."
    ),
}


def check(files: list[str], solution_text: str | None, excluded: dict[str, str] = EXCLUDED) -> list[Violation]:
    for path, reason in excluded.items():
        if len(reason.strip()) < 10:
            raise ConfigError(f"exclusion '{path}' needs a reason")

    violations: list[Violation] = []
    projects = sorted(f for f in files if f.lower().endswith(PROJECT_EXTENSIONS))

    by_dir: dict[str, list[str]] = defaultdict(list)
    for project in projects:
        by_dir[project.rpartition("/")[0] or "."].append(project)
    for directory, items in sorted(by_dir.items()):
        if len(items) > 1:
            names = ", ".join(p.rpartition("/")[2] for p in items)
            violations.append(
                Violation(directory, "one-project-per-folder", f"{len(items)} project files: {names}")
            )

    if solution_text is None:
        violations.append(Violation(SOLUTION, "solution", "solution file not found"))
        return violations
    in_solution = {p.replace("\\", "/") for p in SLN_PROJECT.findall(solution_text)}
    for project in projects:
        if not project.startswith(SOLUTION_ROOTS):
            continue
        if project in in_solution or project in excluded:
            continue
        violations.append(
            Violation(project, "solution-membership", f"project is not in {SOLUTION}")
        )
    for path in excluded:
        if path not in projects:
            violations.append(
                Violation(path, "solution-membership", "stale exclusion: project does not exist")
            )
    return violations


def main() -> list[Violation]:
    root = repo_root()
    sln = root / SOLUTION
    text = sln.read_text(encoding="utf-8-sig") if sln.exists() else None
    return check(tracked_files(root), text)


if __name__ == "__main__":
    run_main("project-layout", main)
