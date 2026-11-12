#!/usr/bin/env python3
"""Infrastructure details in tracked files.

Fails when a tracked text file contains:
- a private, shared or link-local IPv4 address (RFC 1918, RFC 6598,
  RFC 3927) or an IPv6 unique local address (RFC 4193);
- a host name under a non-public suffix (``.lan``, ``.local``,
  ``.internal``, ``.home.arpa``...) in a URL, an e-mail address or a
  ``key=value`` setting. Documentation must use the reserved names of
  RFC 2606 / RFC 6761 (``example.com``, ``*.example``, ``*.test``,
  ``localhost``) and documentation addresses (RFC 5737, RFC 3849);
- a match for an optional, maintainer-supplied pattern (see below).

Optional extra patterns come from the ``HYGIENE_EXTRA_PATTERNS`` environment
variable: one case-insensitive regular expression per line. They are kept out
of the repository (a repository variable in CI) and are never printed: a
match is reported by file, line and pattern number only.

Exceptions go in ``allowlist/content.txt`` as
``<path glob> | <rule> | <reason>``.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from hygiene_common import (  # noqa: E402
    ALLOWLIST_DIR,
    AllowEntry,
    ConfigError,
    Violation,
    env,
    path_matches,
    read_allowlist,
    repo_root,
    run_main,
    tracked_files,
)

OCTET = r"(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)"
PRIVATE_IPV4 = re.compile(
    r"(?<![\w.])(?:"
    rf"10\.{OCTET}\.{OCTET}\.{OCTET}"
    rf"|172\.(?:1[6-9]|2\d|3[01])\.{OCTET}\.{OCTET}"
    rf"|192\.168\.{OCTET}\.{OCTET}"
    rf"|100\.(?:6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.{OCTET}\.{OCTET}"
    rf"|169\.254\.{OCTET}\.{OCTET}"
    r")(?![\w]|\.\d)"
)
ULA_IPV6 = re.compile(r"(?<![\w:])f[cd][0-9a-f]{2}:(?:[0-9a-f]{0,4}:){1,6}[0-9a-f]{0,4}(?![\w:])", re.IGNORECASE)
INTERNAL_SUFFIXES = r"(?:lan|local|localdomain|internal|intranet|corp|home|private|home\.arpa)"
INTERNAL_HOST = re.compile(
    r"(?:://(?:[^\s/@'\"]*@)?|@|=)"
    rf"((?:[a-z0-9-]+\.)+{INTERNAL_SUFFIXES})"
    r"(?=$|[:/\s'\"<>),;\]])",
    re.IGNORECASE,
)

MAX_BYTES = 5 * 1024 * 1024


def load_extra_patterns(raw: str) -> list[re.Pattern[str]]:
    patterns = []
    for index, line in enumerate((l.strip() for l in raw.splitlines()), 1):
        if not line or line.startswith("#"):
            continue
        try:
            patterns.append(re.compile(line, re.IGNORECASE))
        except re.error:
            raise ConfigError(f"extra pattern #{index} is not a valid regular expression") from None
    return patterns


def is_allowed(path: str, rule: str, entries: list[AllowEntry]) -> bool:
    return any(path_matches(path, e.fields[0]) and e.fields[1] == rule for e in entries)


def check_text(path: str, text: str, extra: list[re.Pattern[str]]) -> list[Violation]:
    violations: list[Violation] = []
    for number, line in enumerate(text.splitlines(), 1):
        for match in PRIVATE_IPV4.finditer(line):
            violations.append(Violation(path, "private-ip", f"private address {match.group(0)}", number))
        for match in ULA_IPV6.finditer(line):
            violations.append(Violation(path, "private-ip", f"unique local address {match.group(0)}", number))
        for match in INTERNAL_HOST.finditer(line):
            violations.append(Violation(path, "internal-host", f"non-public host name {match.group(1)}", number))
        for index, pattern in enumerate(extra, 1):
            if pattern.search(line):
                violations.append(Violation(path, "extra-pattern", f"matches extra pattern #{index}", number))
    return violations


def check_files(root: Path, files: list[str], extra: list[re.Pattern[str]], entries: list[AllowEntry]) -> list[Violation]:
    violations: list[Violation] = []
    for path in files:
        for index, pattern in enumerate(extra, 1):
            if pattern.search(path):
                violations.append(Violation(path, "extra-pattern", f"file path matches extra pattern #{index}"))
        full = root / path
        if not full.is_file() or full.stat().st_size > MAX_BYTES:
            continue
        data = full.read_bytes()
        if b"\0" in data[:8192]:
            continue
        text = data.decode("utf-8", errors="replace")
        for v in check_text(path, text, extra):
            if not is_allowed(path, v.rule, entries):
                violations.append(v)
    return violations


def main() -> list[Violation]:
    root = repo_root()
    entries = read_allowlist(ALLOWLIST_DIR / "content.txt", key_fields=2)
    extra = load_extra_patterns(env("HYGIENE_EXTRA_PATTERNS"))
    if extra:
        print(f"content: {len(extra)} extra pattern(s) loaded")
    return check_files(root, tracked_files(root), extra, entries)


if __name__ == "__main__":
    run_main("content", main)
