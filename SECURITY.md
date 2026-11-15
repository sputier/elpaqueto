# Security policy

ElPaqueto is a self-hosted server for private NuGet packages. Security reports are taken seriously and handled privately.

## Reporting a vulnerability

**Do not open a public issue, discussion or pull request for a security problem.** Public reports expose every deployment before a fix exists.

Report vulnerabilities privately through GitHub private vulnerability reporting:

1. Open the [Security tab](https://github.com/sputier/ElPaqueto/security) of this repository.
2. Select **Report a vulnerability**.
3. Fill in the advisory form.

Direct link: <https://github.com/sputier/ElPaqueto/security/advisories/new>

There is no e-mail channel. If you cannot use GitHub private vulnerability reporting, open a public issue that only asks for a private contact, without any detail about the vulnerability.

A useful report includes:

- the affected version, image tag or commit;
- the configuration involved (database and storage providers, reverse proxy, authentication settings), with secrets removed;
- steps to reproduce, or a proof of concept;
- the impact as you understand it.

Never include real passwords, API keys, `Authorization` headers or connection strings. Use placeholders.

## Supported versions

| Version | Supported |
| --- | --- |
| Before the first release: the latest commit of `develop` and `main` | Yes |
| After the first release: the latest minor release | Yes |
| Any older release or commit | No |

Fixes are not backported to older versions. Upgrade to a supported version to receive them.

## Process and timeline

ElPaqueto is a small project. The targets below are best effort, not guarantees.

1. **Acknowledgement** within 7 days of the report.
2. **Triage and initial assessment** within 30 days: whether the report is accepted, its severity, and the affected versions.
3. **Fix** developed in a private fork attached to the advisory, then released.
4. **Advisory**: a GitHub Security Advisory is published with the fix. A CVE is requested through GitHub when the issue warrants one.
5. **Coordinated disclosure**: details are made public when the fix is released, or 90 days after the report, whichever comes first. The date can be moved by mutual agreement, for example when a fix needs more time or when the issue is already being exploited.

Reporters are kept informed at each step in the private advisory.

## Scope

In scope:

- code in this repository, including code inherited from upstream [BaGetter](https://github.com/bagetter/BaGetter) as shipped by ElPaqueto;
- the container image and default configuration built from this repository.

When a vulnerability also affects upstream BaGetter, ElPaqueto coordinates with the upstream project before disclosure.

Out of scope:

- vulnerabilities in third-party dependencies (NuGet packages, client-side libraries, base container images): report them to the project concerned. A report showing that ElPaqueto uses a dependency in a vulnerable way is in scope;
- deployments that ignore the documented requirements, for example a server exposed over plain HTTP on a network;
- social engineering, physical attacks, and denial of service by volumetric flooding;
- findings from automated scanners without a demonstrated impact.

## Credit

Reporters are credited in the published advisory, under the name or handle they choose, unless they ask to stay anonymous. ElPaqueto has no bug bounty program.
