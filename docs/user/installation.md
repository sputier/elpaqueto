# Installation

ElPaqueto runs as a Linux container (recommended) or as a .NET application. In both cases it listens on plain HTTP and expects a TLS-terminating reverse proxy or tunnel in front of it for any client on another machine: see [Running behind a reverse proxy or tunnel](reverse-proxy.md).

No release and no container image are published yet: build the image or the application from this repository.

## Docker

```bash
docker build -t elpaqueto:local .
docker volume create elpaqueto-data
docker run -d --name elpaqueto \
  --read-only --tmpfs /tmp \
  --cap-drop ALL --security-opt no-new-privileges \
  -v elpaqueto-data:/data \
  -p 127.0.0.1:5000:8080 \
  -e Security__PublicBaseUrl=https://nuget.example.test \
  -e Security__ReverseProxy__KnownProxies__0=192.0.2.1 \
  elpaqueto:local
```

`192.0.2.1` stands for the address your reverse proxy connects from. [Running with Docker](docker.md) describes every option: the non-root user (UID 1654), port `8080`, the `/data` volume, why `/tmp` must stay writable, the health check, secrets in `/run/secrets` and bind-mount ownership.

Then create the first administrator inside the container. The password is prompted for, without echo:

```bash
docker exec -it elpaqueto dotnet ElPaqueto.dll admin bootstrap --username admin
```

See [Administration](administration.md) for the other ways to give the password, and for users and API keys.

## Docker Compose

[`docker-compose.example.yml`](../../docker-compose.example.yml) runs the same hardened container without any published port, on a network shared with the reverse proxy, with the first administrator's password as a Compose secret. Copy it, replace every placeholder, then:

```bash
docker compose -f docker-compose.example.yml up -d
docker compose -f docker-compose.example.yml run --rm elpaqueto admin bootstrap --username admin --password-file /run/secrets/elpaqueto_admin_password
```

The comments in the file explain how to create the secret file with the right owner and mode.

## Trying it on one machine

For a first look, without a proxy, the [quick start](../../readme.md#quick-start-docker-local-evaluation) publishes the port on `127.0.0.1` and sets `Security__Transport__RequireHttps=false`, because a request from the Docker host does not reach the container from loopback. Never keep that setting on a server that other machines can reach.

## From a build (advanced)

The server is an ASP.NET Core application. With the .NET 10 SDK:

```bash
dotnet publish src/ElPaqueto -c Release -o ./elpaqueto
cd elpaqueto
dotnet ElPaqueto.dll --urls http://127.0.0.1:5000
```

- `appsettings.json` is read from the working directory, or from the folder named by `ELPAQUETO_CONFIG_ROOT`. Settings can also come from environment variables and `/run/secrets` (see [configuration](configuration.md)).
- By default the package database (`elpaqueto.db`), the security database (`auth.db`) and the packages are created in the working directory. Set `Database:ConnectionString`, `Security:Database:ConnectionString` and `Storage:Path` to put them elsewhere, and restrict that folder to the account that runs the server.
- Requests from loopback may use plain HTTP; every other client must come through HTTPS (`Security:Transport:RequireHttps`). Put a reverse proxy on the same machine in front of it, which loopback trust covers by default.
- Create the first administrator with `dotnet ElPaqueto.dll admin bootstrap --username admin`, from the same folder and with the same settings as the server.

Running the server as a Windows service, a systemd unit or under IIS is possible with the usual ASP.NET Core hosting options but is not documented or tested by this project.

## Next steps

1. [Reverse proxy](reverse-proxy.md): HTTPS, `Security:PublicBaseUrl` and the trusted proxy.
2. [Users, permissions and API keys](administration.md).
3. [Client setup](client-setup.md).
4. [Backups](backup.md).
