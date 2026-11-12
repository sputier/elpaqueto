#!/usr/bin/env python3
"""Run every repository hygiene check, as the CI 'hygiene' job does.

Usage:
  python3 eng/hygiene/run_all.py [--range <revision range>] [--skip-vulnerable]

--range            commits to check for authorship and secrets
                   (default: origin/develop..HEAD)
--skip-vulnerable  skip the vulnerable-package check (needs the .NET SDK and
                   a restored solution)

Runs all checks even when one fails, then exits 1 if any failed.
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from hygiene_common import repo_root  # noqa: E402
from install_tools import install  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--range", default="origin/develop..HEAD")
    parser.add_argument("--skip-vulnerable", action="store_true")
    args = parser.parse_args()

    root = repo_root()
    python = sys.executable
    workflows = sorted(str(p) for p in (root / ".github" / "workflows").glob("*.y*ml"))
    steps: list[tuple[str, list[str]]] = [
        ("self-tests", [python, "-m", "unittest", "discover", "-s", str(HERE / "tests")]),
        ("actionlint", [str(install("actionlint")), *workflows]),
        ("workflows", [python, str(HERE / "check_workflows.py")]),
        ("project-layout", [python, str(HERE / "check_project_layout.py")]),
        ("forbidden-files", [python, str(HERE / "check_forbidden_files.py")]),
        ("content", [python, str(HERE / "check_content.py")]),
        ("secrets", [python, str(HERE / "check_secrets.py"), args.range]),
        ("commits", [python, str(HERE / "check_commits.py"), args.range]),
    ]
    if not args.skip_vulnerable:
        steps.append(("vulnerable-packages", [python, str(HERE / "check_vulnerable_packages.py")]))

    # Unbuffered children keep their output in order with the group markers.
    child_env = {**os.environ, "PYTHONUNBUFFERED": "1"}
    failed = []
    for name, command in steps:
        print(f"::group::{name}" if _in_ci() else f"==> {name}", flush=True)
        code = subprocess.run(command, cwd=root, env=child_env).returncode
        if _in_ci():
            print("::endgroup::", flush=True)
        if code != 0:
            failed.append(name)
            print(f"::error::hygiene check '{name}' failed" if _in_ci() else f"FAILED: {name}", flush=True)

    print("hygiene: all checks passed" if not failed else f"hygiene: failed checks: {', '.join(failed)}")
    return 1 if failed else 0


def _in_ci() -> bool:
    return os.environ.get("GITHUB_ACTIONS") == "true"


if __name__ == "__main__":
    sys.exit(main())
