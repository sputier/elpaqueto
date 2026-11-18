# Known limitations

What ElPaqueto does not do, or has not validated yet. Read this page before you rely on it. [Client compatibility](client-compatibility.md) lists the NuGet clients that are tested, and how.

## Not released yet

- No release, no version tag and no published container image: build the image from this repository ([installation](installation.md)). The release workflow has never published anything.
- The upgrade of a running installation from one ElPaqueto image to the next cannot be tested before a first release.

## Clients and platforms

- **Validated clients: the `dotnet` CLI of the .NET 10 SDK**, on Windows and Linux, and **nuget.exe 7.9.0 on Windows**, by the end-to-end tests (push, restore, search, unlist, delete, refusals), and the `msbuild -t:restore` that Visual Studio runs. The `dotnet` CLI is also covered by the container smoke test. See [client compatibility](client-compatibility.md).
- **Not validated:** the **Visual Studio and Rider interfaces** (a [manual checklist](client-checklist.md) exists, and nobody has run it yet), `nuget.exe` on Mono, VS Code, Paket, PowerShellGet, any client on macOS, and debuggers fetching symbols. They use the same NuGet protocol and configuration, but their prompts, retries and credential storage have not been observed.
- **`nuget list` shows nothing against a V3 source**, including ElPaqueto: nuget.exe deprecated it. Use `nuget search` or `dotnet package search`.
- **The `dotnet` CLI does not wait for `Retry-After`.** A throttled (429) or busy (503) restore fails after a few quick retries instead of waiting. See [what the real clients do](../technical/e2e-tests.md#what-the-real-clients-do).
- **An unlisted version is still chosen by a version range** in restore, as with upstream BaGetter and nuget.org: unlisting only hides a version from search.
- **TLS is not tested automatically.** CI checks only the syntax of the nginx, Caddy and Traefik samples; no automated test runs a client over HTTPS through a proxy or a tunnel.
- The container image is built and smoke-tested in CI on Linux for `linux/amd64` and `linux/arm64`. Outside a container, the server runs in the test suites on Windows and Linux; Windows service, systemd and IIS hosting are not tested.
- The web UI has no automated browser test: its headers and content security policy are tested over HTTP, not in a browser.

## Security model

- **Single node.** Sign-in throttling counters, the password verification cache and the record of failed attempts live in memory, per process: they reset on restart and are not shared between instances. Running several instances behind a load balancer multiplies the attempts an attacker gets.
- **Clients behind one NAT** share one throttling budget. An attacker with many addresses can fill the record of failed attempts; retries of the same wrong password are then counted one by one again, as described in [sign-in throttling](../technical/throttling.md).
- **HTTP Basic only.** There is no web login page, no session and no logout: the browser keeps the credentials until it closes. NuGet clients store or cache credentials as configured on each machine. No OpenID Connect, LDAP, SAML or other external identity provider.
- **Feed-wide permissions.** `Read` gives access to every package, and to the statistics page; there are no per-package or per-feed permissions.
- **Users cannot be deleted**, only disabled.
- **Web UI administration does not exist.** Users and keys are managed with the `admin` commands on the server, which do not authenticate their caller: shell access to the server is administrative access.
- **No HTTPS listener in the recommended setup.** TLS is terminated by a reverse proxy or tunnel. Plain HTTP is accepted from loopback only, unless `Security:Transport:RequireHttps` is turned off.
- **The web pages' content security policy allows inline and evaluated script** (`'unsafe-inline'`, `'unsafe-eval'`), which the inherited UI needs. Protection against injected script comes from the server never emitting package-supplied markup ([web security](../technical/web-security.md)).
- **Data protection keys are kept in memory**: a web form opened before a restart must be reloaded ([Docker](docker.md#data-protection-keys)).
- **On Windows, ElPaqueto does not set file permissions** on the security database: it inherits the permissions of its folder, which you must restrict ([security database](../technical/security-database.md)).
- `AllowPackageOverwrites` and the retention settings let a `Publish` key replace or remove versions when they are enabled by the server's configuration.
- With `Mirror:Enabled`, the server makes outbound requests to the upstream feed for packages it does not have, on authorized reads.

See the [threat model](../technical/threat-model.md) for the residual risks and the reasons behind them.

## Storage and data

- ElPaqueto inherits BaGetter's storage, database and search providers. Two combinations are tested here: SQLite for both databases with the file system for packages (the default, tested by every suite), and **PostgreSQL 17 for the package database** with the file system for packages and the security database in SQLite (the production setup of the [Compose sample](docker.md#with-postgresql)). The SQL Server package database, the Azure Table database, and the cloud storage providers (AWS S3, Azure Blob, Google Cloud Storage, Alibaba Cloud OSS, Tencent COS) are built but not tested by this project. The MySQL provider is excluded from the build until its EF Core 10 support exists ([#55](https://github.com/sputier/ElPaqueto/issues/55)). The security database is always SQLite.
- **What the PostgreSQL tests cover** (`tests/ElPaqueto.Tests/PostgreSqlIntegrationTests.cs`, CI job `postgresql`, a real PostgreSQL 17 container, the real host): migrations on an empty database and on one that is already migrated (also with a connection left open, and with two servers starting together), the `citext` columns, push, restore, search, autocomplete, registration and metadata (with a dependency), unlist and relist, hard delete, case-insensitive ids and versions, restart on an existing database, 120 concurrent requests on a pool of 5 connections, concurrent pushes, and the health endpoint (healthy, then unhealthy when the database is gone). A script brings the Compose sample up (PostgreSQL, ElPaqueto, Caddy with TLS), pushes and restores a package with the `dotnet` CLI and checks the data after a restart and after `down` / `up`. **Not tested:** other PostgreSQL major versions, a PostgreSQL server with TLS or on another host, a PostgreSQL that restarts or fails over while requests run, a database that is not empty when ElPaqueto first starts (for example one shared with another application), upgrades from a PostgreSQL database written by BaGetter (the repair of the `Version` column is tested on a state rebuilt by hand), several ElPaqueto instances on one database (the `auth.db` of each would differ anyway), and any load beyond the concurrency above.
- **Found while testing PostgreSQL, inherited from BaGetter and not fixed here.** A hard delete removes the package row but leaves its dependency rows in `PackageDependencies` with no package (the foreign key is optional by design: [`AbstractContext`](../../src/ElPaqueto.Core/Entities/AbstractContext.cs)); they are harmless and only grow with the number of hard deletes. Two pushes of the same new version at the very same moment can make the loser fail with `500` instead of `409` (the file storage), the stored data stays consistent; the client's retry gets `409`.
- **SQLite allows one writer at a time.** Writes are rare (administration, debounced last-use times of API keys); the databases use write-ahead logging and a busy timeout.
- A hard delete leaves the empty version folder in the file storage.
- Backup and restore have no automated test. The [backup](backup.md) procedure for a stopped container is the recommended one; the online variant is described but not tested.
- **Upgrading from BaGetter** requires renaming `BAGET_CONFIG_ROOT` and the default database file, and recreating users and keys: BaGetter's `ApiKey` and `Authentication` settings stop the server ([upgrading from BaGetter](upgrading-from-bagetter.md)). The upgrade was tested with a database created under BaGetter's file name, not with a database written by BaGetter itself.
- Upstream fixes, security fixes included, reach ElPaqueto only when they are ported ([porting upstream changes](../upstream-porting.md)).

## Project

- The ElPaqueto logo is not under the MIT License of the code: it is all rights reserved, with a nominative-use allowance. It was generated with an AI image tool, so its copyright status cannot be guaranteed ([assets/branding](../../assets/branding/README.md)).
- No Helm chart or Kubernetes manifests are provided: the inherited BaGetter chart was removed because it deployed the upstream image without any of ElPaqueto's security settings. Deploy with the [Docker image and the Compose example](docker.md).
