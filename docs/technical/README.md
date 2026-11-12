# ElPaqueto technical documentation

How ElPaqueto is built, for contributors, reviewers and porters of upstream changes. User-facing behaviour and settings are in the [user documentation](../user/README.md). Decisions and their alternatives are recorded in the ADRs, which these pages link to rather than repeat:

- [ADR 0001: project identity and rebranding scope](../adr/0001-project-identity-and-rebranding-scope.md)
- [ADR 0002: authentication architecture](../adr/0002-authentication-architecture.md)

## Overview

| Page | Content |
| --- | --- |
| [Architecture](architecture.md) | Components, module boundaries, the single registration entry point and the startup sequence. |
| [Request pipeline](request-pipeline.md) | The middleware order of `Startup.Configure` and how a request is answered `401`, `403`, `429` or served. |
| [Data model](data-model.md) | The security database: users, API keys, constraints, migrations. |
| [Secrets handling](secrets-handling.md) | Password hashing, API key generation and storage, logging redaction, configuration secrets. |
| [Threat model](threat-model.md) | Assets, actors, trust boundaries, STRIDE table with the tests that back each mitigation, residual risks. |
| [Testing](testing.md) | Test projects, how to run them, what each level covers. |

## Security module, in detail

| Page | Content |
| --- | --- |
| [Authentication](authentication.md) | The handler, which credential decides, the password flow, the verification cache. |
| [Authorization](authorization.md) | The permission table, deny-by-default, write routes. |
| [Adding a protected endpoint](adding-a-protected-endpoint.md) | What to do when a route is added, including by an upstream port. |
| [API keys](api-keys.md) | Token format, generation, storage, validation order, last-use tracking. |
| [Password hashing](passwords.md) | Algorithm, parameters, normalization, timing. |
| [Sign-in throttling](throttling.md) | Counters, algorithm, limits. |
| [Forwarded headers, public base URL and HTTPS policy](forwarded-headers.md) | Trusted proxies and transport security. |
| [Web security](web-security.md) | CORS, response headers, content security policy, Markdown rendering. |
| [Audit logging](audit-logging.md) | Event catalogue and what is never logged. |
| [Security database](security-database.md) | Location, file permissions, startup migration, SQLite settings. |
| [Administration commands](admin-cli.md) | Wiring and behaviour of the `admin` commands. |
| [Compatibility with BaGetter installations](bagetter-compatibility.md) | Refused legacy settings and renamed defaults. |

## Working with upstream

- [Porting upstream changes](../upstream-porting.md): the procedure and the security checklist.
- [Upstream touch map](upstream-touch-map.md): every inherited file ElPaqueto changed, and why.

## Writing rules

These pages describe what exists in the code at merge time and mark anything planned as such. They reference code by path, never by line number. Diagrams are Mermaid code blocks, which GitHub renders. A pull request that changes the behaviour described here updates the page in the same pull request.
