"""Shared helpers for the repository hygiene checks.

Every check prints one line per violation, in the form
``<path>[:<line>]: [<rule>] <message>``, and exits with status 1 when it found
at least one violation, 2 on a usage or configuration error, 0 otherwise.
"""

from __future__ import annotations

import datetime as _dt
import fnmatch
import os
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

HYGIENE_DIR = Path(__file__).resolve().parent
ALLOWLIST_DIR = HYGIENE_DIR / "allowlist"


class ConfigError(Exception):
    """Raised when an allow-list or an argument is invalid."""


@dataclass
class Violation:
    path: str
    rule: str
    message: str
    line: int | None = None

    def __str__(self) -> str:
        location = self.path if self.line is None else f"{self.path}:{self.line}"
        return f"{location}: [{self.rule}] {self.message}"


def repo_root(start: Path | None = None) -> Path:
    out = subprocess.run(
        ["git", "rev-parse", "--show-toplevel"],
        cwd=start or Path.cwd(),
        check=True,
        capture_output=True,
        text=True,
    )
    return Path(out.stdout.strip())


def tracked_files(root: Path) -> list[str]:
    """Paths of the files tracked by git, relative to ``root``, with ``/``."""
    out = subprocess.run(
        ["git", "ls-files", "-z"],
        cwd=root,
        check=True,
        capture_output=True,
    )
    return [p for p in out.stdout.decode("utf-8").split("\0") if p]


@dataclass
class AllowEntry:
    fields: list[str]
    reason: str
    expires: _dt.date | None
    source: str


def read_allowlist(
    path: Path, key_fields: int, with_expiry: bool = False
) -> list[AllowEntry]:
    """Read a ``|``-separated allow-list.

    Each non-empty, non-comment line holds ``key_fields`` key fields, then an
    expiry date (``YYYY-MM-DD``) when ``with_expiry`` is set, then a reason.
    An entry without a reason, with a malformed date or with an expired date
    is a configuration error: exceptions must stay justified and reviewed.
    """
    entries: list[AllowEntry] = []
    if not path.exists():
        return entries
    errors: list[str] = []
    today = _dt.date.today()
    for number, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        parts = [p.strip() for p in line.split("|")]
        expected = key_fields + (2 if with_expiry else 1)
        source = f"{path.name}:{number}"
        if len(parts) < expected:
            errors.append(f"{source}: expected {expected} '|'-separated fields")
            continue
        keys = parts[:key_fields]
        expires = None
        if with_expiry:
            try:
                expires = _dt.date.fromisoformat(parts[key_fields])
            except ValueError:
                errors.append(f"{source}: expiry must be a YYYY-MM-DD date")
                continue
            if expires < today:
                errors.append(f"{source}: entry expired on {expires.isoformat()}")
                continue
        reason = " | ".join(parts[expected - 1 :]).strip()
        if any(not k for k in keys):
            errors.append(f"{source}: empty key field")
            continue
        if len(reason) < 10:
            errors.append(f"{source}: a reason of at least 10 characters is required")
            continue
        entries.append(AllowEntry(keys, reason, expires, source))
    if errors:
        raise ConfigError("invalid allow-list entries:\n  " + "\n  ".join(errors))
    return entries


def path_matches(path: str, pattern: str) -> bool:
    return fnmatch.fnmatchcase(path, pattern)


def report(check: str, violations: list[Violation]) -> int:
    for v in violations:
        print(str(v))
    if violations:
        print(f"{check}: {len(violations)} violation(s)", file=sys.stderr)
        return 1
    print(f"{check}: OK")
    return 0


def run_main(check: str, func) -> None:
    """Run ``func`` and translate its result into the process exit status."""
    try:
        sys.exit(report(check, func()))
    except ConfigError as error:
        print(f"{check}: configuration error: {error}", file=sys.stderr)
        sys.exit(2)


def env(name: str, default: str = "") -> str:
    return os.environ.get(name, default)
