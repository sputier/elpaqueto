#!/usr/bin/env python3
"""Forbidden tracked files.

Fails on tracked private keys, certificates, key stores, environment files,
local databases and local settings. Exceptions go in
``allowlist/forbidden-files.txt`` as ``<path> | <reason>``.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from hygiene_common import (  # noqa: E402
    ALLOWLIST_DIR,
    Violation,
    path_matches,
    read_allowlist,
    repo_root,
    run_main,
    tracked_files,
)

# Patterns are matched against the file name, case-insensitively.
FORBIDDEN = (
    ("*.pfx", "certificate or key store"),
    ("*.p12", "certificate or key store"),
    ("*.pem", "certificate or private key"),
    ("*.key", "private key"),
    ("*.snk", "strong-name key pair"),
    (".env", "environment file"),
    ("*.db", "local database"),
    ("*.db-wal", "local database journal"),
    ("*.db-shm", "local database journal"),
    ("appsettings.*.local.json", "local settings"),
)


def check(files: list[str], allowed: set[str]) -> list[Violation]:
    violations: list[Violation] = []
    for path in files:
        name = path.rpartition("/")[2].lower()
        for pattern, kind in FORBIDDEN:
            if path_matches(name, pattern) and path not in allowed:
                violations.append(
                    Violation(path, "forbidden-file", f"{kind} ({pattern}) must not be committed")
                )
                break
    for path in sorted(allowed):
        if path not in files:
            violations.append(
                Violation(path, "forbidden-file", "stale allow-list entry: file is not tracked")
            )
    return violations


def main() -> list[Violation]:
    root = repo_root()
    entries = read_allowlist(ALLOWLIST_DIR / "forbidden-files.txt", key_fields=1)
    return check(tracked_files(root), {e.fields[0] for e in entries})


if __name__ == "__main__":
    run_main("forbidden-files", main)
