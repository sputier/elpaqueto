# Architecture decision records

This folder holds the architecture decision records (ADRs) of ElPaqueto. An ADR
records one significant decision: the context that forced it, the decision
itself, its consequences and the alternatives that were rejected.

ADRs are plain Markdown files rendered by GitHub. They live outside the
Docusaurus content folder (`docs/docs/`), so they do not depend on how the user
documentation is built or published.

## Index

| ADR | Title | Status |
|---|---|---|
| [0001](0001-project-identity-and-rebranding-scope.md) | Project identity and rebranding scope | Accepted |
| [0002](0002-authentication-architecture.md) | Authentication and authorization architecture | Accepted |

## Writing a new ADR

1. Copy the template below into `NNNN-short-title.md`, where `NNNN` is the next
   free number, zero-padded to four digits.
2. Fill in every section. Link the GitHub issues that motivate the decision and
   the ones it affects.
3. Add a row to the index above in the same pull request.

An accepted ADR is not rewritten when the decision changes. Write a new ADR,
set the old one to "Superseded by NNNN" and link both ways.

Statuses: `Proposed`, `Accepted`, `Deprecated`, `Superseded by NNNN`.

## Template

```markdown
# NNNN. Title

- Status: Proposed
- Date: YYYY-MM-DD
- Issues: #N

## Context

What forces the decision: the problem, the constraints, the facts.

## Decision

What is decided, stated so that a later change can be checked against it.

## Consequences

What becomes easier, what becomes harder, and the follow-up work it creates.

## Alternatives

The options that were considered and why each was rejected.
```
