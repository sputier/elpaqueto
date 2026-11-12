#!/usr/bin/env python3
"""Workflow rules that actionlint does not cover.

Fails when a workflow under .github/workflows/:
- has no top-level ``permissions:`` block;
- uses an action that is not pinned to a 40-character commit SHA (local
  ``./`` actions excepted; ``docker://`` images must be pinned by digest);
- is triggered by ``pull_request_target``;
- runs on a ``self-hosted`` runner;
- is triggered by ``pull_request`` and references a secret other than
  ``GITHUB_TOKEN`` (or passes ``secrets: inherit``). This is stricter than a
  per-job rule on purpose: a job condition is easy to get wrong;
- grants a publishing scope (``contents``, ``packages``, ``id-token`` or
  ``attestations`` set to ``write``, or ``write-all``) while it can be
  triggered by anything other than a tag push or ``workflow_dispatch``. A
  publishing workflow is never run by a pull request, a branch push, a
  schedule or another workflow (``workflow_call``, ``workflow_run``).
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from hygiene_common import Violation, repo_root, run_main  # noqa: E402

USES = re.compile(r"""^\s*(?:-\s*)?uses:\s*['"]?([^'"\s]+)""")
SHA_PIN = re.compile(r"@[0-9a-f]{40}$")
DIGEST_PIN = re.compile(r"@sha256:[0-9a-f]{64}$")
TOP_LEVEL_KEY = re.compile(r"^([A-Za-z_][\w-]*|'on'|\"on\"):")
SECRET_REF = re.compile(r"\bsecrets\.([A-Za-z_][A-Za-z0-9_]*)")
SECRETS_INHERIT = re.compile(r"^\s*secrets:\s*inherit\b")
PUBLISH_SCOPE = re.compile(
    r"^\s*(?:(?:contents|packages|id-token|attestations)\s*:\s*write|permissions\s*:\s*write-all)\b"
)
PUBLISH_EVENTS = {"push", "workflow_dispatch"}


def strip_comment(line: str) -> str:
    """Remove a YAML comment (a ``#`` at the start or after whitespace)."""
    match = re.search(r"(^|\s)#", line)
    return line[: match.start()] if match else line


def triggers(lines: list[str]) -> set[str]:
    """Event names of the top-level ``on:`` key."""
    events: set[str] = set()
    inside = False
    child_indent: int | None = None
    for raw in lines:
        line = strip_comment(raw).rstrip()
        if not line.strip():
            continue
        top = TOP_LEVEL_KEY.match(line)
        if top:
            # 'on' is parsed as boolean true by YAML 1.1 tools; both spellings count.
            inside = top.group(1).strip("'\"") in ("on", "true")
            child_indent = None
            if inside:
                events.update(re.findall(r"[A-Za-z_]+", line[top.end():]))
            continue
        if not inside:
            continue
        indent = len(line) - len(line.lstrip())
        if child_indent is None:
            child_indent = indent
        if indent == child_indent:
            item = re.match(r"^\s*(?:-\s*)?([A-Za-z_]+)", line)
            if item:
                events.add(item.group(1))
    return events


def on_block(lines: list[str]) -> list[str]:
    """Lines of the top-level ``on:`` key, its own line included."""
    block: list[str] = []
    inside = False
    for raw in lines:
        line = strip_comment(raw).rstrip()
        if not line.strip():
            continue
        top = TOP_LEVEL_KEY.match(line)
        if top:
            inside = top.group(1).strip("'\"") in ("on", "true")
        if inside:
            block.append(line)
    return block


def push_is_tags_only(lines: list[str]) -> bool:
    """True when the ``push`` trigger is filtered on tags and not on branches."""
    block = on_block(lines)
    keys = {m.group(1) for l in block if (m := re.match(r"^\s+([A-Za-z_-]+)\s*:", l))}
    return "tags" in keys and not keys & {"branches", "branches-ignore", "tags-ignore"}


def check_workflow(path: str, text: str) -> list[Violation]:
    violations: list[Violation] = []
    lines = text.splitlines()

    if not any(re.match(r"^permissions\s*:", strip_comment(l)) for l in lines):
        violations.append(
            Violation(path, "workflow-permissions", "no top-level 'permissions:' block")
        )

    events = triggers(lines)
    if "pull_request_target" in events:
        violations.append(
            Violation(path, "workflow-trigger", "'pull_request_target' is not allowed")
        )
    pr_triggered = "pull_request" in events

    publish_lines = [n for n, raw in enumerate(lines, 1) if PUBLISH_SCOPE.match(strip_comment(raw))]
    if publish_lines:
        other_events = sorted(events - PUBLISH_EVENTS)
        if other_events:
            violations.append(
                Violation(
                    path,
                    "publish-trigger",
                    f"grants a publishing scope but is also triggered by: {', '.join(other_events)}",
                    publish_lines[0],
                )
            )
        if "push" in events and not push_is_tags_only(lines):
            violations.append(
                Violation(
                    path,
                    "publish-trigger",
                    "grants a publishing scope but its push trigger is not limited to tags",
                    publish_lines[0],
                )
            )

    for number, raw in enumerate(lines, 1):
        line = strip_comment(raw)
        uses = USES.match(line)
        if uses:
            ref = uses.group(1)
            if ref.startswith("./"):
                pass
            elif ref.startswith("docker://"):
                if not DIGEST_PIN.search(ref):
                    violations.append(
                        Violation(path, "action-pin", f"'{ref}' is not pinned by digest", number)
                    )
            elif not SHA_PIN.search(ref):
                violations.append(
                    Violation(path, "action-pin", f"'{ref}' is not pinned to a full commit SHA", number)
                )
        if re.search(r"\bself-hosted\b", line):
            violations.append(
                Violation(path, "runner", "self-hosted runners are not allowed", number)
            )
        if pr_triggered:
            for name in SECRET_REF.findall(line):
                if name != "GITHUB_TOKEN":
                    violations.append(
                        Violation(
                            path,
                            "pr-secrets",
                            f"secret '{name}' referenced in a workflow triggered by pull_request",
                            number,
                        )
                    )
            if SECRETS_INHERIT.match(line):
                violations.append(
                    Violation(path, "pr-secrets", "'secrets: inherit' in a workflow triggered by pull_request", number)
                )
    return violations


def check(root: Path) -> list[Violation]:
    violations: list[Violation] = []
    workflow_dir = root / ".github" / "workflows"
    for wf in sorted(list(workflow_dir.glob("*.yml")) + list(workflow_dir.glob("*.yaml"))):
        rel = wf.relative_to(root).as_posix()
        violations.extend(check_workflow(rel, wf.read_text(encoding="utf-8")))
    return violations


if __name__ == "__main__":
    run_main("workflows", lambda: check(repo_root()))
