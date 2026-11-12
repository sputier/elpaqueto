# Known limitations

What ElPaqueto does not do, or has not validated yet. Read this page before you rely on it. A validation report listing the clients and platforms tested end to end is planned; until it exists, this page is the reference.

## Not released yet

- No release, no version tag and no published container image: build the image from this repository ([installation](installation.md)).
- The end-to-end validation with real clients is in progress. The NuGet client libraries are exercised in-process by the integration tests, and the `dotnet` CLI of the .NET 10 SDK by the container smoke test (push with an API key, restore with a password or a key). `nuget.exe`, Visual Studio, Rider, MSBuild-only builds, Paket and PowerShellGet are not validated.
- The container image is built and smoke-tested in CI on Linux for `linux/amd64` and `linux/arm64`. Running the server outside a container is only exercised by the test suite (Linux and Windows) and by development use.

## Security model

- **Single node.** Sign-in throttling counters and the password verification cache live in memory, per process: they reset on restart and are not shared between instances. Running several instances behind a load balancer multiplies the attempts an attacker gets.
- **HTTP Basic only.** There is no web login page, no session and no logout: the browser keeps the credentials until it closes. NuGet clients store or cache credentials as configured on each machine. No OpenID Connect, LDAP, SAML or other external identity provider.
- **Feed-wide permissions.** `Read` gives access to every package; there are no per-package or per-feed permissions.
- **Users cannot be deleted**, only disabled.
- **Web UI administration does not exist.** Users and keys are managed with the `admin` commands on the server, which do not authenticate their caller: shell access to the server is administrative access.
- **No HTTPS listener in the recommended setup.** TLS is terminated by a reverse proxy or tunnel. Plain HTTP is accepted from loopback only, unless `Security:Transport:RequireHttps` is turned off.
- **Clients behind one NAT** share one throttling budget.
- `AllowPackageOverwrites` and the retention settings let a `Publish` key replace or remove versions when they are enabled by the server's configuration.
- With `Mirror:Enabled`, the server makes outbound requests to the upstream feed for packages it does not have.

See the [threat model](../technical/threat-model.md) for the residual risks and the reasons behind them.

## Inherited features not validated

ElPaqueto inherits BaGetter's storage, database and search providers. Only the defaults are tested here: SQLite for both databases and the file system for packages. The PostgreSQL and SQL Server package databases, the Azure Table database, and the cloud storage providers (AWS S3, Azure Blob, Google Cloud Storage, Alibaba Cloud OSS, Tencent COS) are built but not tested by this project. The MySQL provider is excluded from the build. The security database is always SQLite.

## Documentation

The inherited documentation site under `docs/docs/` and the Helm chart under `deployment templates/` come from BaGetter and are not maintained for ElPaqueto: their fate is still being decided. Use the [user documentation](README.md).
