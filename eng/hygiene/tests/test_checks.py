"""Fixture-based self-tests for the hygiene checks.

Run: python3 -m unittest discover -s eng/hygiene/tests

Fixture values that would themselves trip the content check (private
addresses, internal host names) are assembled at run time.
"""

from __future__ import annotations

import datetime as dt
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import check_commits  # noqa: E402
import check_content  # noqa: E402
import check_forbidden_files  # noqa: E402
import check_project_layout  # noqa: E402
import check_trivyignore  # noqa: E402
import check_vulnerable_packages  # noqa: E402
import check_workflows  # noqa: E402
from hygiene_common import ConfigError, read_allowlist  # noqa: E402

SHA = "0123456789abcdef0123456789abcdef01234567"


def rules(violations):
    return sorted(v.rule for v in violations)


class WorkflowTests(unittest.TestCase):
    GOOD = f"""name: ok
on:
  push:
    branches: [develop]
  pull_request:
permissions:
  contents: read
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@{SHA} # v1
      - uses: ./.github/actions/local
      - run: echo "${{{{ secrets.GITHUB_TOKEN }}}}"
"""

    def test_compliant_workflow_passes(self):
        self.assertEqual(check_workflows.check_workflow("wf.yml", self.GOOD), [])

    def test_missing_permissions(self):
        text = self.GOOD.replace("permissions:\n  contents: read\n", "")
        self.assertEqual(rules(check_workflows.check_workflow("wf.yml", text)), ["workflow-permissions"])

    def test_job_level_permissions_do_not_count(self):
        text = self.GOOD.replace("permissions:\n  contents: read\n", "").replace(
            "    runs-on:", "    permissions:\n      contents: read\n    runs-on:"
        )
        self.assertEqual(rules(check_workflows.check_workflow("wf.yml", text)), ["workflow-permissions"])

    def test_unpinned_actions(self):
        for ref in ("actions/checkout@v4", "actions/checkout@main", f"actions/checkout@{SHA[:7]}", "docker://alpine:3"):
            with self.subTest(ref=ref):
                text = self.GOOD.replace(f"actions/checkout@{SHA}", ref)
                self.assertEqual(rules(check_workflows.check_workflow("wf.yml", text)), ["action-pin"])

    def test_pull_request_target(self):
        for trigger in ("  pull_request_target:\n", "  - pull_request_target\n"):
            text = self.GOOD.replace("  pull_request:\n", trigger)
            self.assertEqual(rules(check_workflows.check_workflow("wf.yml", text)), ["workflow-trigger"])
        inline = self.GOOD.replace("on:\n  push:\n    branches: [develop]\n  pull_request:\n", "on: [push, pull_request_target]\n")
        self.assertEqual(rules(check_workflows.check_workflow("wf.yml", inline)), ["workflow-trigger"])

    def test_self_hosted_runner(self):
        text = self.GOOD.replace("runs-on: ubuntu-latest", "runs-on: [self-hosted, linux]")
        self.assertEqual(rules(check_workflows.check_workflow("wf.yml", text)), ["runner"])

    def test_secret_in_pull_request_workflow(self):
        text = self.GOOD.replace("secrets.GITHUB_TOKEN", "secrets.DEPLOY_KEY")
        self.assertEqual(rules(check_workflows.check_workflow("wf.yml", text)), ["pr-secrets"])
        text = self.GOOD + "  reuse:\n    uses: ./.github/workflows/x.yml\n    secrets: inherit\n"
        self.assertEqual(rules(check_workflows.check_workflow("wf.yml", text)), ["pr-secrets"])

    def test_secret_allowed_without_pull_request_trigger(self):
        text = self.GOOD.replace("  pull_request:\n", "").replace("secrets.GITHUB_TOKEN", "secrets.DEPLOY_KEY")
        self.assertEqual(check_workflows.check_workflow("wf.yml", text), [])

    def test_pull_request_in_expression_is_not_a_trigger(self):
        text = self.GOOD.replace("  pull_request:\n", "").replace(
            "secrets.GITHUB_TOKEN", "secrets.DEPLOY_KEY }} ${{ github.event.pull_request.number"
        )
        self.assertEqual(check_workflows.check_workflow("wf.yml", text), [])

    def test_comments_are_ignored(self):
        text = self.GOOD + "      # - uses: actions/setup-node@v4 on self-hosted\n"
        self.assertEqual(check_workflows.check_workflow("wf.yml", text), [])

    RELEASE = f"""name: release
on:
  push:
    tags: ['v*.*.*']
  workflow_dispatch:
    inputs:
      dry-run:
        type: boolean
        default: true
permissions: {{}}
jobs:
  publish:
    runs-on: ubuntu-latest
    permissions:
      contents: write
      packages: write
      id-token: write
      attestations: write
    steps:
      - uses: actions/checkout@{SHA} # v1
"""

    def test_publishing_workflow_on_tags_and_dispatch_passes(self):
        self.assertEqual(check_workflows.check_workflow("release.yml", self.RELEASE), [])
        tags_only = self.RELEASE.replace("  workflow_dispatch:\n    inputs:\n      dry-run:\n        type: boolean\n        default: true\n", "")
        self.assertEqual(check_workflows.check_workflow("release.yml", tags_only), [])

    def test_publishing_scope_with_other_triggers_fails(self):
        for trigger in (
            "  pull_request:\n",
            "  pull_request_target:\n",
            "  workflow_call:\n",
            "  workflow_run:\n    workflows: [CI]\n",
            "  schedule:\n    - cron: '0 0 * * 0'\n",
            "  issue_comment:\n",
        ):
            with self.subTest(trigger=trigger):
                text = self.RELEASE.replace("  workflow_dispatch:\n", trigger + "  workflow_dispatch:\n")
                self.assertIn("publish-trigger", rules(check_workflows.check_workflow("release.yml", text)))

    def test_publishing_scope_on_branch_push_fails(self):
        for push in ("  push:\n    branches: [main]\n", "  push:\n    tags: ['v*']\n    branches: [main]\n", "  push:\n"):
            with self.subTest(push=push):
                text = self.RELEASE.replace("  push:\n    tags: ['v*.*.*']\n", push)
                self.assertEqual(rules(check_workflows.check_workflow("release.yml", text)), ["publish-trigger"])
        inline = self.RELEASE.replace(
            "on:\n  push:\n    tags: ['v*.*.*']\n  workflow_dispatch:\n    inputs:\n      dry-run:\n        type: boolean\n        default: true\n",
            "on: [push, workflow_dispatch]\n",
        )
        self.assertEqual(rules(check_workflows.check_workflow("release.yml", inline)), ["publish-trigger"])

    def test_each_publishing_scope_is_detected(self):
        for scope in ("contents: write", "packages: write", "id-token: write", "attestations: write"):
            with self.subTest(scope=scope):
                text = self.GOOD.replace("permissions:\n  contents: read\n", f"permissions:\n  {scope}\n")
                self.assertEqual(rules(check_workflows.check_workflow("wf.yml", text)), ["publish-trigger", "publish-trigger"])
        write_all = self.GOOD.replace("permissions:\n  contents: read\n", "permissions: write-all\n")
        self.assertIn("publish-trigger", rules(check_workflows.check_workflow("wf.yml", write_all)))

    def test_read_and_security_events_scopes_are_not_publishing(self):
        text = self.GOOD.replace("permissions:\n  contents: read\n", "permissions:\n  contents: read\n  security-events: write\n  packages: read\n")
        self.assertEqual(check_workflows.check_workflow("wf.yml", text), [])


class ProjectLayoutTests(unittest.TestCase):
    SLN = 'Project("{X}") = "A", "src\\A\\A.csproj", "{Y}"\nEndProject\n'

    def test_compliant_layout(self):
        self.assertEqual(check_project_layout.check(["src/A/A.csproj"], self.SLN, {}), [])

    def test_two_projects_in_one_folder(self):
        files = ["src/A/A.csproj", "src/A/A - Backup.csproj"]
        found = check_project_layout.check(files, self.SLN + self.SLN.replace("A.csproj", "A - Backup.csproj"), {})
        self.assertEqual(rules(found), ["one-project-per-folder"])

    def test_project_missing_from_solution(self):
        found = check_project_layout.check(["src/A/A.csproj", "tests/B/B.csproj"], self.SLN, {})
        self.assertEqual([(v.path, v.rule) for v in found], [("tests/B/B.csproj", "solution-membership")])

    def test_projects_outside_solution_roots_are_ignored(self):
        self.assertEqual(check_project_layout.check(["src/A/A.csproj", "eng/T/T.csproj"], self.SLN, {}), [])

    def test_exclusion_needs_reason_and_existing_project(self):
        with self.assertRaises(ConfigError):
            check_project_layout.check(["tests/B/B.csproj"], self.SLN, {"tests/B/B.csproj": ""})
        found = check_project_layout.check(["src/A/A.csproj"], self.SLN, {"src/Gone/Gone.csproj": "Removed long ago."})
        self.assertEqual(rules(found), ["solution-membership"])


class ForbiddenFileTests(unittest.TestCase):
    def test_forbidden_names(self):
        for path in ("certs/test.pfx", "a/b.P12", "key.pem", "id.key", "sign.snk", "docs/.env",
                     "data/app.db", "app.db-wal", "app.db-shm", "src/appsettings.Development.local.json"):
            with self.subTest(path=path):
                self.assertEqual(rules(check_forbidden_files.check([path], set())), ["forbidden-file"])

    def test_allowed_names(self):
        files = ["docs/.env.example", "src/appsettings.json", "db/schema.sql", "README.md", "monkey.cs"]
        self.assertEqual(check_forbidden_files.check(files, set()), [])

    def test_allow_list(self):
        self.assertEqual(check_forbidden_files.check(["tests/fixture.pem"], {"tests/fixture.pem"}), [])
        self.assertEqual(rules(check_forbidden_files.check([], {"gone.pem"})), ["forbidden-file"])


class ContentTests(unittest.TestCase):
    def test_private_addresses(self):
        for address in ("10." + "1.2.3", "172." + "16.0.1", "172." + "31.255.255", "192." + "168.1.10",
                        "100." + "64.0.1", "169." + "254.1.1", "fd" + "12:3456::1"):
            with self.subTest(address=address):
                found = check_content.check_text("f", f"host: {address}:8080", [])
                self.assertEqual(rules(found), ["private-ip"])

    def test_public_and_documentation_addresses(self):
        for text in ("192.0.2.10", "198.51.100.7", "203.0.113.1", "172.32.0.1", "8.8.8.8",
                     "version 2018.10.28.07.42", "2001:db8::1", "127.0.0.1", "v1.10.0.0.1"):
            with self.subTest(text=text):
                self.assertEqual(check_content.check_text("f", text, []), [])

    def test_internal_host_names(self):
        for text in ("https://nuget." + "lan/v3/index.json", "user@build." + "internal", "Server=db." + "corp;",
                     "http://nas." + "home.arpa:5000"):
            with self.subTest(text=text):
                self.assertEqual(rules(check_content.check_text("f", text, [])), ["internal-host"])

    def test_reserved_names_and_code_are_accepted(self):
        for text in ("https://nuget.example.com/v3/index.json", "http://server.test", "http://localhost:5000",
                     "namespace ElPaqueto.Protocol.Internal;", "using X.Internal;"):
            with self.subTest(text=text):
                self.assertEqual(check_content.check_text("f", text, []), [])

    def test_extra_patterns_are_reported_without_content(self):
        extra = check_content.load_extra_patterns("# comment\nsecret-host\n")
        found = check_content.check_text("f", "see SECRET-HOST here", extra)
        self.assertEqual(rules(found), ["extra-pattern"])
        self.assertNotIn("secret-host", str(found[0]).lower())

    def test_invalid_extra_pattern(self):
        with self.assertRaises(ConfigError):
            check_content.load_extra_patterns("(unclosed")


class CommitTests(unittest.TestCase):
    @staticmethod
    def commit(message="fix: something", author=("Jane Doe", "jane@example.com"), committer=None):
        committer = committer or author
        return check_commits.Commit(SHA, author[0], author[1], committer[0], committer[1], message)

    def test_human_commit_passes(self):
        msg = "feat: add x\n\nCo-authored-by: John Roe <john@example.com>\nSigned-off-by: Jane Doe <jane@example.com>"
        self.assertEqual(check_commits.check_commit(self.commit(msg)), [])

    def test_web_merge_and_dependency_bot_pass(self):
        self.assertEqual(check_commits.check_commit(self.commit(committer=("GitHub", "noreply@github.com"))), [])
        bot = ("dependabot[bot]", "49699333+dependabot[bot]@users.noreply.github.com")
        self.assertEqual(check_commits.check_commit(self.commit(author=bot)), [])

    def test_person_sharing_a_tool_name_passes(self):
        person = ("Claude Dupont", "claude.dupont@example.com")
        msg = "docs: fix typo\n\nCo-authored-by: Claude Martin <claude.martin@example.com>"
        self.assertEqual(check_commits.check_commit(self.commit(msg, author=person)), [])

    def test_attribution_trailers_fail(self):
        for trailer in ("Co-authored-by: Claude <noreply@anthropic.com>",
                        "Co-Authored-By: Claude Opus 4 <noreply@example.com>",
                        "co-authored-by: Copilot <175728472+Copilot@users.noreply.github.com>",
                        "Assisted-by: ChatGPT",
                        "Generated-by: Gemini 2.5 Pro",
                        "Co-authored-by: Aider <aider@aider.chat>"):
            with self.subTest(trailer=trailer):
                found = check_commits.check_commit(self.commit(f"feat: x\n\n{trailer}"))
                self.assertEqual(rules(found), ["commit-attribution"])

    def test_generated_with_line_fails(self):
        msg = "feat: x\n\nGenerated with [Claude Code](https://claude.com/claude-code)"
        self.assertEqual(rules(check_commits.check_commit(self.commit(msg))), ["commit-attribution"])

    def test_tool_identities_fail(self):
        for identity in (("Claude", "noreply@anthropic.com"), ("Codex", "codex@openai.com"),
                         ("Cursor Agent", "cursoragent@cursor.com"), ("Jane", "jane@anthropic.com")):
            with self.subTest(identity=identity):
                human = ("Jane Doe", "jane@example.com")
                found = check_commits.check_commit(self.commit(author=identity, committer=human))
                self.assertEqual(rules(found), ["commit-identity"])

    def test_other_bot_accounts_fail(self):
        bot = ("some-agent[bot]", "1+some-agent[bot]@users.noreply.github.com")
        self.assertEqual(rules(check_commits.check_commit(self.commit(committer=bot))), ["commit-identity"])

    def test_extra_patterns_apply_to_messages(self):
        extra = check_content.load_extra_patterns("private-name")
        found = check_commits.check_commit(self.commit("fix: Private-Name leak"), extra)
        self.assertEqual(rules(found), ["extra-pattern"])


class AllowlistTests(unittest.TestCase):
    def read(self, text, **kwargs):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "list.txt"
            path.write_text(text, encoding="utf-8")
            return read_allowlist(path, **kwargs)

    def test_valid_entries(self):
        future = (dt.date.today() + dt.timedelta(days=30)).isoformat()
        entries = self.read(f"# c\n\nGHSA-1 | {future} | No fixed version yet, not reachable.\n", key_fields=1, with_expiry=True)
        self.assertEqual(entries[0].fields, ["GHSA-1"])

    def test_missing_reason(self):
        with self.assertRaises(ConfigError):
            self.read("a.pem\n", key_fields=1)
        with self.assertRaises(ConfigError):
            self.read("a.pem | ok\n", key_fields=1)

    def test_missing_or_expired_expiry(self):
        with self.assertRaises(ConfigError):
            self.read("GHSA-1 | Not reachable from our code.\n", key_fields=1, with_expiry=True)
        past = (dt.date.today() - dt.timedelta(days=1)).isoformat()
        with self.assertRaises(ConfigError):
            self.read(f"GHSA-1 | {past} | Not reachable from our code.\n", key_fields=1, with_expiry=True)


class VulnerablePackageTests(unittest.TestCase):
    REPORT = {
        "version": 1,
        "projects": [{
            "path": "src/A/A.csproj",
            "frameworks": [{
                "framework": "net10.0",
                "topLevelPackages": [{"id": "Pkg.High", "resolvedVersion": "1.0.0", "vulnerabilities": [
                    {"severity": "High", "advisoryurl": "https://github.com/advisories/GHSA-aaaa-bbbb-cccc"}]}],
                "transitivePackages": [
                    {"id": "Pkg.Moderate", "resolvedVersion": "2.0.0", "vulnerabilities": [
                        {"severity": "Moderate", "advisoryurl": "https://github.com/advisories/GHSA-dddd-eeee-ffff"}]},
                    {"id": "Pkg.Critical", "resolvedVersion": "3.0.0", "vulnerabilities": [
                        {"severity": "Critical", "advisoryurl": "https://github.com/advisories/GHSA-gggg-hhhh-iiii"}]},
                ],
            }],
        }],
    }

    def test_high_and_critical_fail_moderate_is_reported(self):
        violations, reported = check_vulnerable_packages.evaluate(check_vulnerable_packages.parse(self.REPORT), set())
        self.assertEqual(sorted(v.message.split()[0] for v in violations), ["Pkg.Critical", "Pkg.High"])
        self.assertEqual([f.package for f in reported], ["Pkg.Moderate"])

    def test_allow_listed_advisory_is_reported_not_failed(self):
        violations, reported = check_vulnerable_packages.evaluate(
            check_vulnerable_packages.parse(self.REPORT), {"GHSA-aaaa-bbbb-cccc", "GHSA-gggg-hhhh-iiii"})
        self.assertEqual(violations, [])
        self.assertEqual(len(reported), 3)

    def test_no_vulnerabilities(self):
        report = {"version": 1, "projects": [{"path": "src/A/A.csproj"}]}
        self.assertEqual(check_vulnerable_packages.parse(report), [])

    def test_tool_errors_are_configuration_errors(self):
        with self.assertRaises(ConfigError):
            check_vulnerable_packages.parse({"problems": [{"level": "error", "text": "not restored"}]})


class TrivyIgnoreTests(unittest.TestCase):
    TODAY = dt.date(2026, 6, 1)
    REASON = "# Not reachable from our code.\n"

    def check(self, text):
        return rules(check_trivyignore.check_text(".trivyignore", text, self.TODAY))

    def test_justified_entries_pass(self):
        text = (
            "# Accepted findings\n\n"
            "# No fixed version yet; the vulnerable parser is never reached.\n"
            "CVE-2026-12345 exp:2026-09-01\n"
            "# Advisory for a feature disabled in the image.\n"
            "GHSA-abcd-efgh-ijkl exp:2027-06-01\n"
        )
        self.assertEqual(self.check(text), [])

    def test_empty_file_passes(self):
        self.assertEqual(self.check(""), [])

    def test_entry_without_expiry_fails(self):
        self.assertEqual(self.check(self.REASON + "CVE-2026-1\n"), ["trivyignore-format"])

    def test_malformed_expired_or_distant_expiry_fails(self):
        self.assertEqual(self.check(self.REASON + "CVE-2026-1 exp:2026-13-01\n"), ["trivyignore-format"])
        self.assertEqual(self.check(self.REASON + "CVE-2026-1 exp:2026-05-31\n"), ["trivyignore-expired"])
        self.assertEqual(self.check(self.REASON + "CVE-2026-1 exp:2027-06-03\n"), ["trivyignore-expiry"])

    def test_entry_without_justification_fails(self):
        self.assertEqual(self.check("CVE-2026-1 exp:2026-09-01\n"), ["trivyignore-reason"])
        self.assertEqual(self.check("# short\nCVE-2026-1 exp:2026-09-01\n"), ["trivyignore-reason"])
        self.assertEqual(self.check(self.REASON + "\nCVE-2026-1 exp:2026-09-01\n"), ["trivyignore-reason"])

    def test_one_comment_does_not_justify_two_entries(self):
        text = self.REASON + "CVE-2026-1 exp:2026-09-01\nCVE-2026-2 exp:2026-09-01\n"
        self.assertEqual(self.check(text), ["trivyignore-reason"])

    def test_duplicate_entry_fails(self):
        entry = self.REASON + "CVE-2026-1 exp:2026-09-01\n"
        self.assertEqual(self.check(entry + entry), ["trivyignore-duplicate"])


if __name__ == "__main__":
    unittest.main()
