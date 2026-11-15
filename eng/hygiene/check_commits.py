#!/usr/bin/env python3
"""Authorship rules for new commits.

Every commit in the checked range must be authored and committed by a
person. The check fails when a commit:
- has an author or committer identity that belongs to an automated
  code-generation tool (by name or e-mail domain);
- has a ``[bot]`` author or committer other than the allowed automation
  accounts (``dependabot[bot]``, ``github-actions[bot]``);
- carries a trailer (``Co-authored-by``, ``Assisted-by``, ``Generated-by``...)
  or a "generated with/by" line that credits such a tool;
- matches an optional maintainer-supplied pattern
  (``HYGIENE_EXTRA_PATTERNS``, see check_content.py) in its message.

Usage: check_commits.py [<revision range>]   (default: origin/develop..HEAD)
"""

from __future__ import annotations

import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from check_content import load_extra_patterns  # noqa: E402
from hygiene_common import ConfigError, Violation, env, repo_root, run_main  # noqa: E402

# Names of code-generation assistants and agents, as they appear in commit
# identities and attribution trailers. Matched as whole words.
TOOL_NAMES = (
    r"claude(?:[\s-]?code)?",
    r"anthropic",
    r"openai",
    r"chatgpt",
    r"codex",
    r"gpt-?\d[\w.-]*",
    r"copilot",
    r"gemini(?:[\s-]?cli)?",
    r"cursor[\s-]?agent",
    r"cursoragent",
    r"aider",
    r"devin-ai",
    r"codeium",
    r"windsurf",
    r"tabnine",
    r"codewhisperer",
    r"amazon q(?: developer)?",
    r"sweep-ai",
)
TOOL_DOMAINS = (
    "anthropic.com",
    "claude.ai",
    "claude.com",
    "openai.com",
    "chatgpt.com",
    "cursor.com",
    "cursor.sh",
    "cognition.ai",
    "aider.chat",
    "codeium.com",
    "windsurf.com",
    "tabnine.com",
)
ALLOWED_BOTS = ("dependabot[bot]", "github-actions[bot]")

TOOL_ALTERNATION = "(?:" + "|".join(TOOL_NAMES) + ")"
# Words that follow a tool name in a model or product identity
# ("<tool> Opus 4", "<tool> 2.5 Pro", "<tool> Agent"). A person whose first
# name happens to be a tool name ("<tool> Dupont") does not match.
MODEL_WORD = r"(?:opus|sonnet|haiku|fable|code|cli|agent|assistant|chat|pro|flash|ultra|max|mini|turbo|developer|\d[\w.]*)"
TOOL = re.compile(r"(?<![\w-])" + TOOL_ALTERNATION + r"(?![\w-])", re.IGNORECASE)
TOOL_NAME = re.compile(r"^\s*" + TOOL_ALTERNATION + r"(?:[\s-]+" + MODEL_WORD + r")*\s*$", re.IGNORECASE)
DOMAIN = re.compile(r"(?:@|//|\.|\b)(?:" + "|".join(re.escape(d) for d in TOOL_DOMAINS) + r")\b", re.IGNORECASE)
IDENTITY = re.compile(r"^\s*(.*?)\s*(?:<([^>]*)>)?\s*$")
TRAILER = re.compile(
    r"^\s*(co-authored-by|assisted-by|generated-by|helped-by|signed-off-by|reviewed-by|"
    r"suggested-by|made-with|written-by|ai-assisted|tool)\s*:\s*(.+)$",
    re.IGNORECASE,
)
GENERATED_LINE = re.compile(r"\b(?:generated|created|written|authored|assisted|made|produced)\s+(?:with|by|using|via)\b", re.IGNORECASE)
BOT = re.compile(r"\[bot\]", re.IGNORECASE)

SEPARATOR = "\x1e"
FIELD = "\x1f"
LOG_FORMAT = FIELD.join(["%H", "%an", "%ae", "%cn", "%ce", "%B"]) + SEPARATOR


@dataclass
class Commit:
    sha: str
    author_name: str
    author_email: str
    committer_name: str
    committer_email: str
    message: str


def is_tool_identity(name: str, email: str) -> bool:
    """True when a name/e-mail pair identifies a code-generation tool."""
    return bool(TOOL_NAME.match(name) or DOMAIN.search(email))


def credits_tool(text: str) -> bool:
    """True when free text (a 'generated with' line) names a tool."""
    return bool(TOOL.search(text) or DOMAIN.search(text))


def trailer_credits_tool(value: str) -> bool:
    match = IDENTITY.match(value)
    name, email = (match.group(1), match.group(2) or "") if match else (value, "")
    return is_tool_identity(name, email) or bool(DOMAIN.search(name))


def check_identity(commit: Commit, role: str, name: str, email: str) -> list[Violation]:
    short = commit.sha[:12]
    violations = []
    if is_tool_identity(name, email):
        violations.append(Violation(short, "commit-identity", f"{role} identity belongs to a code-generation tool"))
    elif (BOT.search(name) or BOT.search(email)) and name not in ALLOWED_BOTS:
        violations.append(Violation(short, "commit-identity", f"{role} is a non-human account '{name}'"))
    return violations


def check_commit(commit: Commit, extra: list[re.Pattern[str]] | None = None) -> list[Violation]:
    short = commit.sha[:12]
    violations: list[Violation] = []
    violations += check_identity(commit, "author", commit.author_name, commit.author_email)
    violations += check_identity(commit, "committer", commit.committer_name, commit.committer_email)
    for line in commit.message.splitlines():
        trailer = TRAILER.match(line)
        if trailer and trailer_credits_tool(trailer.group(2)):
            violations.append(
                Violation(short, "commit-attribution", f"'{trailer.group(1)}' trailer credits a code-generation tool")
            )
        elif GENERATED_LINE.search(line) and credits_tool(line):
            violations.append(Violation(short, "commit-attribution", "message credits a code-generation tool"))
    for index, pattern in enumerate(extra or [], 1):
        if pattern.search(commit.message):
            violations.append(Violation(short, "extra-pattern", f"message matches extra pattern #{index}"))
    return violations


def read_commits(root: Path, revision_range: str) -> list[Commit]:
    result = subprocess.run(
        ["git", "log", "--no-color", f"--format={LOG_FORMAT}", revision_range],
        cwd=root,
        capture_output=True,
    )
    if result.returncode != 0:
        raise ConfigError(f"git log {revision_range} failed: {result.stderr.decode().strip()}")
    commits = []
    for record in result.stdout.decode("utf-8", errors="replace").split(SEPARATOR):
        record = record.strip("\n")
        if not record:
            continue
        fields = record.split(FIELD)
        if len(fields) != 6:
            raise ConfigError("unexpected git log output")
        commits.append(Commit(*fields))
    return commits


def main() -> list[Violation]:
    revision_range = sys.argv[1] if len(sys.argv) > 1 else "origin/develop..HEAD"
    root = repo_root()
    commits = read_commits(root, revision_range)
    print(f"commits: checking {len(commits)} commit(s) in {revision_range}")
    extra = load_extra_patterns(env("HYGIENE_EXTRA_PATTERNS"))
    violations: list[Violation] = []
    for commit in commits:
        violations += check_commit(commit, extra)
    return violations


if __name__ == "__main__":
    run_main("commits", main)
