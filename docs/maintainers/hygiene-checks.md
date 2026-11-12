# Repository hygiene checks

The `hygiene` job of `.github/workflows/ci.yml` enforces repository rules
that are specific to this project and easy to break by accident. It runs on
every pull request and every push to `develop` and `main`, with
`contents: read` only and no secrets.

The checks live in `eng/hygiene/` and run locally with the same result.

## Running the checks locally

Requirements: Python 3.10 or later, git, and, for the vulnerable-package
check, the .NET SDK pinned in `global.json`. Linux x64 and Windows x64 are
supported (the pinned tools are downloaded for these platforms).

```sh
dotnet restore ElPaqueto.sln
python3 eng/hygiene/run_all.py
```

On Windows, use `python` instead of `python3` (PowerShell or Git Bash).

`run_all.py` runs every check, even after a failure, and exits with status 1
if any failed. Options:

- `--range <revision range>`: commits inspected by the authorship and secret
  checks (default `origin/develop..HEAD`);
- `--skip-vulnerable`: skip the vulnerable-package check when the .NET SDK is
  not available.

Each check can also run on its own, for example
`python3 eng/hygiene/check_project_layout.py`. A violation is printed as
`<path>[:<line>]: [<rule>] <message>`.

Third-party tools (`actionlint`, `gitleaks`) are downloaded from their GitHub
releases by `eng/hygiene/install_tools.py`, verified against the SHA-256
recorded in that script and cached in `eng/hygiene/.tools/` (ignored by git).

## Rules

| Check | Script | Fails when |
|---|---|---|
| Self-tests | `tests/test_checks.py` | a fixture-based test of the scripts fails |
| Workflow lint | `actionlint` | a workflow has a syntax, expression or shell error |
| Workflow rules | `check_workflows.py` | a workflow has no top-level `permissions:`; an action is not pinned to a 40-character commit SHA (local `./` actions excepted, `docker://` images must be pinned by digest); a workflow uses `pull_request_target` or a `self-hosted` runner; a workflow triggered by `pull_request` references a secret other than `GITHUB_TOKEN` or uses `secrets: inherit`; a workflow that grants a publishing scope (`contents`, `packages`, `id-token` or `attestations` set to `write`, or `write-all`) can be triggered by anything other than a tag push (`push` with `tags` and no branch filter) or `workflow_dispatch` |
| Project layout | `check_project_layout.py` | a folder contains more than one project file; a project under `src/`, `tests/` or `samples/` is not in `ElPaqueto.sln` |
| Forbidden files | `check_forbidden_files.py` | a tracked file matches `*.pfx`, `*.p12`, `*.pem`, `*.key`, `*.snk`, `.env`, `*.db`, `*.db-wal`, `*.db-shm` or `appsettings.*.local.json` |
| Infrastructure details | `check_content.py` | a tracked text file contains a private, shared or link-local IPv4 address, an IPv6 unique local address, or a host name under a non-public suffix such as `.lan`, `.local`, `.internal` or `.home.arpa` in a URL, an e-mail address or a `key=value` setting |
| Secrets | `check_secrets.py` (gitleaks) | a secret is found in the tracked files or in the patches of the inspected commits |
| Commit authorship | `check_commits.py` | an inspected commit is authored or committed by a code-generation tool or by a `[bot]` account other than `dependabot[bot]` and `github-actions[bot]`, or credits such a tool in a trailer (`Co-authored-by`, `Assisted-by`...) or a "generated with" line |
| Vulnerable packages | `check_vulnerable_packages.py` | `dotnet list ElPaqueto.sln package --vulnerable --include-transitive` reports a High or Critical advisory; Low and Moderate advisories are listed in the job summary without failing |

Examples in documentation and tests use reserved names (`example.com`,
`*.example`, `*.test`, `localhost`) and documentation addresses
(`192.0.2.0/24`, `198.51.100.0/24`, `203.0.113.0/24`, `2001:db8::/32`).

## Exceptions

Exceptions are reviewed like code. Every entry needs a reason; an entry
without one makes the check fail with a configuration error.

| Check | Where | Format |
|---|---|---|
| Project layout | `EXCLUDED` in `check_project_layout.py` | project path and reason |
| Forbidden files | `eng/hygiene/allowlist/forbidden-files.txt` | `<path> \| <reason>` |
| Infrastructure details | `eng/hygiene/allowlist/content.txt` | `<path glob> \| <rule> \| <reason>` |
| Vulnerable packages | `eng/hygiene/allowlist/vulnerable-packages.txt` | `<advisory id> \| <expiry YYYY-MM-DD> \| <reason>` |
| Secrets | `.gitleaksignore` | gitleaks fingerprint, preceded by a comment giving the reason |

A vulnerability exception must have an expiry date; once expired, the check
fails until the entry is reviewed again. A real secret is never allow-listed:
revoke it, then remove it from the history if needed.

To request an exception, add the entry in the pull request that needs it and
explain in the description why the rule does not apply.

## Maintainer-supplied patterns

The content and commit checks also accept extra patterns that must not be
stored in the repository, such as names of private hosts. Set them in the
repository variable `HYGIENE_EXTRA_PATTERNS` (*Settings → Secrets and
variables → Actions → Variables*), one case-insensitive regular expression
per line, or in the environment variable of the same name when running
locally. A match is reported by file, line and pattern number only; the
pattern and the matched text are never printed.
