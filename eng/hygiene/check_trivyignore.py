#!/usr/bin/env python3
"""Format of the container image scan exceptions (``.trivyignore``).

The image scan of the Docker workflow fails on HIGH and CRITICAL findings.
An accepted finding is listed in ``.trivyignore`` at the repository root, and
every entry must be justified and reviewed:

    # <justification: why the finding does not apply or cannot be fixed yet>
    <finding id> exp:<YYYY-MM-DD>

Fails when an entry:
- is not a finding id (``CVE-...``, ``GHSA-...``...) followed by ``exp:<date>``
  (Trivy stops ignoring the finding after that date);
- has an expiry date that is malformed, already past, or more than a year
  ahead (an exception is re-reviewed at least once a year);
- is not directly preceded by a comment line of at least 10 characters;
- is listed twice.

Usage: check_trivyignore.py [<path>]   (default: .trivyignore)
"""

from __future__ import annotations

import datetime as dt
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from hygiene_common import Violation, repo_root, run_main  # noqa: E402

ENTRY = re.compile(r"^(?P<id>[A-Z][A-Z0-9]*-[A-Za-z0-9][A-Za-z0-9._:-]*)\s+exp:(?P<date>\S+)$")
MAX_VALIDITY = dt.timedelta(days=366)


def check_text(path: str, text: str, today: dt.date | None = None) -> list[Violation]:
    today = today or dt.date.today()
    violations: list[Violation] = []
    seen: dict[str, int] = {}
    previous = ""
    for number, raw in enumerate(text.splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            previous = line
            continue
        match = ENTRY.match(line)
        if not match:
            violations.append(Violation(path, "trivyignore-format", "expected '<finding id> exp:<YYYY-MM-DD>'", number))
            previous = line
            continue
        finding, date = match.group("id"), match.group("date")
        try:
            expires = dt.date.fromisoformat(date)
        except ValueError:
            violations.append(Violation(path, "trivyignore-format", f"{finding}: expiry must be a YYYY-MM-DD date", number))
        else:
            if expires < today:
                violations.append(Violation(path, "trivyignore-expired", f"{finding}: expired on {expires.isoformat()}", number))
            elif expires - today > MAX_VALIDITY:
                violations.append(
                    Violation(path, "trivyignore-expiry", f"{finding}: expiry more than a year ahead", number)
                )
        if not previous.startswith("#") or len(previous.lstrip("#").strip()) < 10:
            violations.append(
                Violation(path, "trivyignore-reason", f"{finding}: needs a justification comment on the line above", number)
            )
        if finding in seen:
            violations.append(
                Violation(path, "trivyignore-duplicate", f"{finding}: already listed on line {seen[finding]}", number)
            )
        else:
            seen[finding] = number
        previous = line
    return violations


def main() -> list[Violation]:
    root = repo_root()
    relative = sys.argv[1] if len(sys.argv) > 1 else ".trivyignore"
    path = root / relative
    if not path.exists():
        return []
    return check_text(relative, path.read_text(encoding="utf-8"))


if __name__ == "__main__":
    run_main("trivyignore", main)
