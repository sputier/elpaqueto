#!/usr/bin/env python3
"""Secret detection with gitleaks (pinned, checksum-verified, see install_tools.py).

Scans:
- the current content of every tracked file;
- the patches of the commits in the given revision range, so that a secret
  added then removed within a pull request is still caught.

Findings are printed redacted. A false positive is excluded by adding its
fingerprint to ``.gitleaksignore`` at the repository root, with a comment
line above it giving the reason; a real secret must be revoked, not ignored.

Usage: check_secrets.py [<revision range>]   (default: origin/develop..HEAD)
"""

from __future__ import annotations

import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from hygiene_common import repo_root, tracked_files  # noqa: E402
from install_tools import install  # noqa: E402


def snapshot(root: Path, target: Path) -> None:
    for path in tracked_files(root):
        source = root / path
        if source.is_file() and not source.is_symlink():
            destination = target / path
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, destination)


def main() -> int:
    revision_range = sys.argv[1] if len(sys.argv) > 1 else "origin/develop..HEAD"
    root = repo_root()
    gitleaks = str(install("gitleaks"))
    common = ["--no-banner", "--redact", "--verbose", "--exit-code", "1"]
    ignore = root / ".gitleaksignore"
    if ignore.exists():
        common += ["--gitleaks-ignore-path", str(ignore)]
    status = 0
    with tempfile.TemporaryDirectory(prefix="hygiene-secrets-") as tmp:
        snapshot(root, Path(tmp))
        print("secrets: scanning tracked files")
        # Scan from inside the snapshot so reported paths (and fingerprints)
        # are relative to the repository root.
        status |= subprocess.run([gitleaks, "dir", *common, "."], cwd=tmp).returncode
    print(f"secrets: scanning commits in {revision_range}")
    status |= subprocess.run(
        [gitleaks, "git", *common, f"--log-opts={revision_range}", str(root)]
    ).returncode
    print("secrets: OK" if status == 0 else "secrets: secret(s) found", file=sys.stderr if status else sys.stdout)
    return 1 if status else 0


if __name__ == "__main__":
    sys.exit(main())
