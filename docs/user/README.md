# ElPaqueto user documentation

These pages are for the people who install, configure and use an ElPaqueto server. The design behind them is in the [technical documentation](../technical/README.md).

## Reading order for a new installation

1. [Installation](installation.md): run the server with Docker, Docker Compose or from a build.
2. [Running behind a reverse proxy or tunnel](reverse-proxy.md): HTTPS, the trusted proxy and the public URL.
3. [First administrator](administration.md#first-administrator): `admin bootstrap`; no account exists until you create one.
4. [Users and permissions](administration.md#managing-users): `admin user` commands and the `Read`, `Publish` and `Admin` permissions.
5. [API keys](administration.md#api-keys): `admin key` commands; creation, expiry, rotation, revocation.
6. [Client setup](client-setup.md): `NuGet.Config`, credentials on each platform and in CI.
7. [Publishing](publishing.md) and [restoring](restoring.md) packages.
8. [Backup and restore](backup.md), then [upgrading](upgrading.md).

## Reference

| Page | Content |
| --- | --- |
| [Installation](installation.md) | Docker, Compose, running from a build, ports and volumes. |
| [Running with Docker](docker.md) | The image's runtime contract: user, port, volume, health check, secrets, hardening flags. |
| [Configuration](configuration.md) | Every `Security:` setting, the inherited server settings, sources and precedence, environment variables and `/run/secrets`. |
| [Administration](administration.md) | The `admin` commands: bootstrap, users, permissions, API keys, exit codes, JSON output. |
| [Client setup](client-setup.md) | NuGet sources and credentials on Windows, Linux, macOS and CI; package source mapping. |
| [Publishing](publishing.md) | `dotnet nuget push`, symbols, duplicates, unlist, delete and relist. |
| [Restoring](restoring.md) | `dotnet restore`, caches, prerelease and unlisted versions. |
| [Running behind a reverse proxy or tunnel](reverse-proxy.md) | What the proxy must do and what ElPaqueto must be told; nginx, Caddy, Traefik, tunnels. |
| [Logging and monitoring](logging.md) | The audit trail, what to alert on, shipping logs. |
| [Backup and restore](backup.md) | What to back up and a tested procedure for the Docker volume. |
| [Upgrading](upgrading.md) | Migrations, backups before an upgrade, rollback limits. |
| [Upgrading from BaGetter](upgrading-from-bagetter.md) | Breaking changes for an existing BaGetter installation. |
| [Troubleshooting](troubleshooting.md) | `401`, `403`, `429`, `503`, `409`, wrong URLs, `NU1301`, `NU1302`. |
| [Known limitations](known-limitations.md) | What ElPaqueto does not do, or has not validated yet. |

Users, permissions and API keys are all managed with the `admin` commands, so the three topics share one page, [Administration](administration.md).

## Conventions

Examples use placeholders: `nuget.example.test` for the server, `<username>`, `<package>`, and a key in a file (`elpaqueto.key`) or an environment variable. Passwords are never given as command-line arguments: the `admin` commands prompt for them or read them from standard input or a file.

The inherited BaGetter documentation site under [`docs/docs/`](../docs/) is not maintained for ElPaqueto: where it disagrees with these pages, these pages are right.
