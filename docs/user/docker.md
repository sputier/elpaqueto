# Running ElPaqueto with Docker

The repository's `Dockerfile` builds a Linux image of the server for `linux/amd64` and `linux/arm64`. No image is published yet: build it yourself. The image is meant to run behind a TLS-terminating reverse proxy or tunnel, see [Running behind a reverse proxy or tunnel](reverse-proxy.md).

The image ships without any account and without any default password.

## Building the image

```bash
docker build -t elpaqueto:local .

# With a version and the commit, published as image labels:
docker build --build-arg Version=1.2.3 --build-arg REVISION="$(git rev-parse HEAD)" -t elpaqueto:1.2.3 .

# Both architectures (needs a buildx builder that can emulate the other one):
docker buildx build --platform linux/amd64,linux/arm64 -t elpaqueto:local .
```

| Build argument | Default | Use |
| --- | --- | --- |
| `Version` | `0.1.0-prerelease` | Assembly version and `org.opencontainers.image.version` label. |
| `REVISION` | empty | Commit SHA, `org.opencontainers.image.revision` label. |
| `SOURCE_URL` | `https://github.com/sputier/ElPaqueto` | `org.opencontainers.image.source` and `.url` labels. A fork sets its own repository. |

The base images (`mcr.microsoft.com/dotnet/sdk` for the build stage, `mcr.microsoft.com/dotnet/aspnet` for the runtime) are pinned to a patch-level Alpine tag; Dependabot proposes updates. The build stage runs on the build machine's own platform and cross-compiles for the target, so only the runtime stage's package installation runs under emulation.

## Runtime contract

| Item | Value |
| --- | --- |
| User | `app`, UID and GID **1654** (the .NET base image's non-root user). Never root. |
| Port | `8080`, plain HTTP (`EXPOSE 8080`). TLS is terminated by the proxy. |
| Volume | `/data` (`VOLUME ["/data"]`): packages, symbols and both databases. |
| Writable paths | `/data` and `/tmp` only. The application files in `/app` belong to root. |
| Health check | `wget -q --spider http://127.0.0.1:8080/health` every 30 s (timeout 5 s, start period 30 s, 3 retries). |
| Stop signal | `SIGTERM`, received by `dotnet` itself (exec-form entry point, PID 1). |
| Shutdown budget | `DOTNET_SHUTDOWNTIMEOUTSECONDS=8`, below the 10 s that `docker stop` waits by default. |
| Internet access | None at run time, unless `Mirror:Enabled` is `true` (the mirror fetches packages from the upstream feed). |

### Default settings

The image sets these environment variables. Override any of them with `-e` or in Compose; keep the paths on the `/data` volume.

| Variable | Value |
| --- | --- |
| `ASPNETCORE_HTTP_PORTS` | `8080` |
| `Storage__Type` / `Storage__Path` | `FileSystem` / `/data` |
| `Database__Type` | `Sqlite` |
| `Database__ConnectionString` | `Data Source=/data/db/elpaqueto.db` |
| `Security__Database__ConnectionString` | `Data Source=/data/db/auth.db` |
| `Search__Type` | `Database` |
| `DOTNET_SHUTDOWNTIMEOUTSECONDS` | `8` |
| `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT` | `false` (cultures and time zones are installed) |

Secrets can also be given as files in `/run/secrets`, see [configuration](configuration.md#sources-and-precedence).

## Running the container

The recommended options make the root filesystem read-only and drop every Linux capability:

```bash
docker volume create elpaqueto-data
docker run -d --name elpaqueto \
  --read-only --tmpfs /tmp \
  --cap-drop ALL --security-opt no-new-privileges \
  -v elpaqueto-data:/data \
  -p 127.0.0.1:5000:8080 \
  -e Security__PublicBaseUrl=https://nuget.example.test \
  -e Security__ReverseProxy__KnownProxies__0=192.0.2.1 \
  elpaqueto:local

docker ps --filter name=elpaqueto    # STATUS shows "(healthy)" after a few seconds
```

`192.0.2.1` stands for the address the proxy connects from; see [the reverse-proxy examples](reverse-proxy.md#example-proxy-on-the-docker-host-port-published-to-it). [`docker-compose.example.yml`](../../docker-compose.example.yml) at the repository root shows the same setup with Compose, without any published port.

A request sent from the Docker host to a published port does not come from loopback inside the container: it is a remote request, refused over plain HTTP with `403 HTTPS is required.` unless it comes through a trusted proxy that sets `X-Forwarded-Proto: https`.

### `/tmp` is required

With `--read-only`, mount a writable `/tmp` (`--tmpfs /tmp`, or a volume):

- uploaded packages are buffered in a temporary file before they are stored. Without a writable `/tmp`, every push fails with `500`;
- .NET creates its diagnostics sockets there (`dotnet-counters`, `dotnet-trace`, `dotnet-dump`). Without `/tmp` the server still starts, without them. Setting `DOTNET_EnableDiagnostics=0` turns them off explicitly, at the cost of those tools.

A tmpfs lives in memory and Docker does not limit its size by default. Size it for the largest package you accept (`MaxPackageSizeGiB`, 8 GiB by default), for example `--tmpfs /tmp:size=2g` with a matching `MaxPackageSizeGiB`, or mount a volume on `/tmp` for large packages.

### Data protection keys

ASP.NET Core data protection (antiforgery tokens of the web pages) keeps its keys in memory in the container: the log shows `Using an in-memory repository. Keys will not be persisted to storage.` at startup. After a restart, a web form opened before the restart must be reloaded. NuGet clients are not affected.

## Volumes and file ownership

`/data` contains:

| Path | Content |
| --- | --- |
| `/data/packages`, `/data/symbols` | Package and symbol files. |
| `/data/db/elpaqueto.db` | Package metadata (SQLite). |
| `/data/db/auth.db` | Users, permissions and API keys (SQLite). Created with mode `0600`. |

Back up the whole volume, ideally while the container is stopped (a stopped server leaves no `-wal` or `-shm` file behind). `auth.db` holds the credentials: protect the backup accordingly.

**Named volume (recommended).** A new, empty named volume is initialised from the image: `/data` and its sub-folders belong to UID 1654, `/data/db` with mode `0700`.

**Bind mount.** A host folder keeps its own owner and mode. Give it to UID 1654 before the first start, otherwise the server stops at startup with `Access to the path '/data/db' is denied`:

```bash
sudo mkdir -p /srv/elpaqueto
sudo chown 1654:1654 /srv/elpaqueto
sudo chmod 0750 /srv/elpaqueto
docker run ... -v /srv/elpaqueto:/data ... elpaqueto:local
```

The server then creates `/data/db` itself, with the process's default mode; restrict it with `chmod 0700` if other host users can reach the folder. Do not run the container as root to work around ownership.

## Health check

The image checks the anonymous liveness endpoint `/health` (the `HealthCheck:Path` setting) from inside the container. The request comes from `127.0.0.1`, which is allowed over plain HTTP. `/health` returns only the overall status (`{"Status":"Healthy"}`); `/health/details` requires the `admin` permission.

If you change `HealthCheck:Path`, override the check to match, for example `--health-cmd 'wget -q --spider http://127.0.0.1:8080/alive || exit 1'` or the `healthcheck` of the Compose example. `PathBase` needs no change: the path without the prefix keeps working. busybox `wget` is available in the image; `curl` is not.

## Stopping

`docker stop` sends `SIGTERM`. The server stops accepting connections, lets in-flight requests finish for up to `DOTNET_SHUTDOWNTIMEOUTSECONDS` (8 s), closes the databases (no `-wal` file is left behind) and exits with code 0. The log shows `Application is shutting down...`.

A request still running after that budget, typically a large upload, is aborted and the client must retry. To give uploads more time, raise both values together, keeping the shutdown budget below the grace period:

```bash
docker run ... -e DOTNET_SHUTDOWNTIMEOUTSECONDS=50 ... elpaqueto:local
docker stop -t 60 elpaqueto
```

With Compose, set `stop_grace_period` instead of `docker stop -t`. If the grace period is shorter than the shutdown budget, Docker kills the process (exit code 137) before it closes the databases.

## Verifying an image

`eng/docker/smoke-test.sh <image>` runs the checks of the Docker workflow against any local image: non-root user, read-only root filesystem, health, anonymous access rules, owner-only security database, graceful stop and restart on the same volume. See [CONTRIBUTING](../../CONTRIBUTING.md#container-image).
