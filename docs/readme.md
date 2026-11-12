# ElPaqueto documentation

| Section | For | Start with |
| --- | --- | --- |
| [User documentation](user/README.md) | People who install, configure and use the server | [Installation](user/installation.md) |
| [Technical documentation](technical/README.md) | Contributors, reviewers, porters of upstream changes | [Architecture](technical/architecture.md) |
| [Architecture decision records](adr/README.md) | Anyone who wants to know why | [ADR 0002: authentication architecture](adr/0002-authentication-architecture.md) |
| [Porting upstream changes](upstream-porting.md) | Maintainers porting BaGetter changes | Section 4, porting procedure |
| [Maintainer notes](maintainers/) | Maintainers | [Repository settings](maintainers/repository-settings.md), [hygiene checks](maintainers/hygiene-checks.md) |

Contributing rules are in [CONTRIBUTING.md](../CONTRIBUTING.md), and the vulnerability reporting process in [SECURITY.md](../SECURITY.md).

## Inherited documentation site

`docs/docs/` and the Docusaurus configuration in this folder (`docusaurus.config.ts`, `sidebars.ts`, `src/`, `static/`, `package.json`) come from BaGetter's documentation website. They are **not maintained for ElPaqueto** and are not published: some pages still describe BaGetter's behaviour or cloud deployments that ElPaqueto does not test. Whether they are removed, or kept and rebuilt, is still to be decided. Use the Markdown pages above.

To preview the inherited site locally, with [Node.js](https://nodejs.org/) and Yarn: `yarn` then `yarn start` in this folder.
