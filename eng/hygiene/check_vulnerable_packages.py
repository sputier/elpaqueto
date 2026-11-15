#!/usr/bin/env python3
"""Known-vulnerable NuGet packages, direct and transitive.

Runs ``dotnet list ElPaqueto.sln package --vulnerable --include-transitive``
(the solution must be restored first) and fails on any High or Critical
advisory. Low and Moderate advisories are reported, and added to the GitHub
job summary when ``GITHUB_STEP_SUMMARY`` is set, without failing.

Exceptions go in ``allowlist/vulnerable-packages.txt`` as
``<advisory id> | <expiry YYYY-MM-DD> | <reason>``. Expired entries and
entries without a reason fail the check.

Usage: check_vulnerable_packages.py [--input <json file>]
"""

from __future__ import annotations

import json
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from hygiene_common import (  # noqa: E402
    ALLOWLIST_DIR,
    ConfigError,
    Violation,
    env,
    read_allowlist,
    repo_root,
    run_main,
)

FAILING = {"high", "critical"}


@dataclass(frozen=True)
class Finding:
    project: str
    package: str
    version: str
    severity: str
    advisory: str
    transitive: bool


def advisory_id(url: str) -> str:
    return url.rstrip("/").rpartition("/")[2] or url


def parse(report: dict, root: Path | None = None) -> list[Finding]:
    findings: set[Finding] = set()
    problems = [p.get("text", "") for p in report.get("problems", []) if p.get("level") == "error"]
    if problems:
        raise ConfigError("dotnet list package reported errors: " + "; ".join(problems))
    for project in report.get("projects", []):
        path = project.get("path", "")
        if root is not None:
            try:
                path = Path(path).resolve().relative_to(root.resolve()).as_posix()
            except ValueError:
                pass
        for framework in project.get("frameworks", []):
            for key, transitive in (("topLevelPackages", False), ("transitivePackages", True)):
                for package in framework.get(key, []):
                    for vuln in package.get("vulnerabilities", []):
                        findings.add(
                            Finding(
                                project=path,
                                package=package.get("id", "?"),
                                version=package.get("resolvedVersion", "?"),
                                severity=vuln.get("severity", "?"),
                                advisory=advisory_id(vuln.get("advisoryurl", "")),
                                transitive=transitive,
                            )
                        )
    return sorted(findings, key=lambda f: (f.project, f.package, f.advisory))


def evaluate(findings: list[Finding], allowed: set[str]) -> tuple[list[Violation], list[Finding]]:
    violations: list[Violation] = []
    reported: list[Finding] = []
    for f in findings:
        kind = "transitive" if f.transitive else "direct"
        if f.severity.lower() in FAILING and f.advisory not in allowed:
            violations.append(
                Violation(
                    f.project,
                    "vulnerable-package",
                    f"{f.package} {f.version} ({kind}): {f.severity} advisory {f.advisory}",
                )
            )
        else:
            reported.append(f)
    return violations, reported


def write_summary(reported: list[Finding]) -> None:
    summary = env("GITHUB_STEP_SUMMARY")
    if not summary:
        return
    with open(summary, "a", encoding="utf-8") as out:
        out.write("### Vulnerable packages (not failing)\n\n")
        if not reported:
            out.write("None.\n\n")
            return
        out.write("| Project | Package | Version | Severity | Advisory |\n|---|---|---|---|---|\n")
        for f in reported:
            out.write(f"| {f.project} | {f.package} | {f.version} | {f.severity} | {f.advisory} |\n")
        out.write("\n")


def run_dotnet(root: Path) -> dict:
    result = subprocess.run(
        ["dotnet", "list", "ElPaqueto.sln", "package", "--vulnerable", "--include-transitive", "--format", "json"],
        cwd=root,
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        raise ConfigError(f"dotnet list package failed (restore first):\n{result.stdout}{result.stderr}")
    return json.loads(result.stdout)


def main() -> list[Violation]:
    root = repo_root()
    entries = read_allowlist(ALLOWLIST_DIR / "vulnerable-packages.txt", key_fields=1, with_expiry=True)
    if len(sys.argv) > 2 and sys.argv[1] == "--input":
        report = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
    else:
        report = run_dotnet(root)
    violations, reported = evaluate(parse(report, root), {e.fields[0] for e in entries})
    for f in reported:
        print(f"note: {f.project}: {f.package} {f.version}: {f.severity} advisory {f.advisory}")
    write_summary(reported)
    return violations


if __name__ == "__main__":
    run_main("vulnerable-packages", main)
