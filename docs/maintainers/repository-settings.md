# Repository settings checklist

Some security controls are repository *settings*, not files, so a pull request
cannot review them. This checklist records the expected value of each setting
and the command that verifies it.

Run the commands from a clone of the repository with the GitHub CLI (`gh`)
authenticated as a repository administrator. `gh api` replaces `{owner}` and
`{repo}` with the current repository.

Record the verification date and the result in the pull request or issue that
changes a setting.

## Files kept in the repository

| File | Purpose |
|---|---|
| `.github/dependabot.yml` | Weekly version updates for NuGet, GitHub Actions, Docker and the npm documentation site, grouped per ecosystem, targeting `develop`. |
| `.github/workflows/ci.yml` | Build and test on Linux and Windows (`build-test`). |
| `.github/workflows/codeql.yml` | CodeQL analysis of C# (manual build) and of the workflows themselves, with the `security-extended` suite, on pull requests, pushes to `develop`/`main` and weekly. |
| `.github/workflows/dependency-review.yml` | Fails a pull request that adds a dependency with a known vulnerability of high or critical severity. |

Every action used by a workflow is pinned to a full commit SHA, with the
release tag in a trailing comment. Dependabot keeps both up to date.

## Code security

- [ ] **Dependency graph** enabled (always on for public repositories).
- [ ] **Dependabot alerts** enabled.

  ```sh
  gh api repos/{owner}/{repo}/vulnerability-alerts --silent && echo enabled
  ```

  Expected: `enabled` (HTTP 204). HTTP 404 means disabled.

- [ ] **Dependabot security updates** enabled.

  ```sh
  gh api repos/{owner}/{repo}/automated-security-fixes
  ```

  Expected: `{"enabled":true,"paused":false}`.

- [ ] **Secret scanning** and **push protection** enabled.

  ```sh
  gh api repos/{owner}/{repo} --jq .security_and_analysis
  ```

  Expected: `secret_scanning.status` and
  `secret_scanning_push_protection.status` are `enabled`.

- [ ] **Private vulnerability reporting** enabled (used by `SECURITY.md`).

  ```sh
  gh api repos/{owner}/{repo}/private-vulnerability-reporting
  ```

  Expected: `{"enabled":true}`.

- [ ] **Dependabot** lists the configured ecosystems under
  *Insights → Dependency graph → Dependabot* after `.github/dependabot.yml`
  reaches the default branch.

## Branch rulesets

A ruleset targets `main` and `develop`:

- [ ] Require a pull request before merging, with at least one approval and a
  review from code owners (once `CODEOWNERS` exists, see below).
- [ ] Require these status checks to pass:
  - `build-test (ubuntu-latest)` and `build-test (windows-latest)` (CI);
  - `analyze (csharp)` and `analyze (actions)` (CodeQL);
  - `dependency-review`.
- [ ] Block force pushes.
- [ ] Block deletions.
- [ ] Bypass allowed for repository administrators only, so that a single
  maintainer is not blocked by the approval rule. Bypasses appear in the
  audit log. Revisit when more maintainers join.

```sh
gh api repos/{owner}/{repo}/rulesets
gh api repos/{owner}/{repo}/rulesets/RULESET_ID
gh api repos/{owner}/{repo}/rules/branches/develop
```

Expected: the ruleset is listed with `enforcement: active`, its
`conditions.ref_name.include` contains `refs/heads/main` and
`refs/heads/develop`, and its rules contain `pull_request`,
`required_status_checks` (with the contexts above), `non_fast_forward` and
`deletion`. A direct `git push` to `develop` is rejected.

## Tag ruleset

- [ ] A tag ruleset on `refs/tags/v*` restricts creation, update and deletion
  of release tags to repository administrators.

```sh
gh api repos/{owner}/{repo}/rulesets --jq '.[] | select(.target == "tag")'
```

## Code owners

- [ ] `.github/CODEOWNERS` assigns `.github/`, `src/ElPaqueto.Security/`,
  `Dockerfile`, `SECURITY.md` and `eng/` to a maintainer team or account.

The file is added by a maintainer once the owning handle is chosen. Do not
commit a placeholder handle: GitHub reports unknown owners as errors.

```sh
gh api repos/{owner}/{repo}/codeowners/errors
```

Expected: `{"errors":[]}`.

## GitHub Actions

- [ ] Allowed actions limited to actions owned by GitHub (and any explicitly
  reviewed pattern).

  ```sh
  gh api repos/{owner}/{repo}/actions/permissions
  gh api repos/{owner}/{repo}/actions/permissions/selected-actions
  ```

- [ ] Default `GITHUB_TOKEN` permissions read-only, and GitHub Actions cannot
  create or approve pull requests.

  ```sh
  gh api repos/{owner}/{repo}/actions/permissions/workflow
  ```

  Expected: `"default_workflow_permissions":"read"` and
  `"can_approve_pull_request_reviews":false`.

- [ ] Approval required before running workflows from outside contributors.

  ```sh
  gh api repos/{owner}/{repo}/actions/permissions/fork-pr-contributor-approval
  ```

  Expected: `{"approval_policy":"all_external_contributors"}`.

## Verification log

| Date | Item | Result |
|---|---|---|
| 2026-11-12 | Dependabot alerts | enabled (HTTP 204) |
| 2026-11-12 | Dependabot security updates | `{"enabled":true,"paused":false}` |
| 2026-11-12 | Secret scanning, push protection | both `enabled` |
| 2026-11-12 | Private vulnerability reporting | `{"enabled":true}` |
| 2026-11-12 | Workflow token permissions | `read`, `can_approve_pull_request_reviews: false` |
| 2026-11-12 | Allowed actions | GitHub-owned only |
| 2026-11-12 | Outside contributor approval | `first_time_contributors`: not yet at the expected value |
| 2026-11-12 | Branch ruleset | exists for `main` only (pull request with one approval, no force push, no deletion); `develop` and required status checks not configured yet |
| 2026-11-12 | Tag ruleset, `CODEOWNERS` | not configured yet |
