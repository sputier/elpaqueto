# Running behind a reverse proxy or tunnel

ElPaqueto does not terminate TLS itself in the recommended setup. It listens on plain HTTP (port 8080 in the container), and a reverse proxy or tunnel in front of it terminates TLS and forwards the requests. This page describes what the server expects from that proxy. The settings are described in the [security configuration reference](configuration.md).

## What the proxy must do

1. **Terminate TLS** and forward the request to ElPaqueto over HTTP.
2. **Set `X-Forwarded-Proto: https`.** Without it, a request from a remote client counts as plain HTTP and is rejected with `403 HTTPS is required.`
3. **Set `X-Forwarded-For`** to the client address, appending to any value the client sent. Most proxies do this by default. Without it, every request seems to come from the proxy itself.
4. **Never redirect to HTTP or downgrade.** ElPaqueto itself never redirects. On the public side, prefer not to listen on plain HTTP at all; a proxy that does should redirect only a browser's bare `GET /` (see [Proxy configurations](#proxy-configurations)).

`X-Forwarded-Host` is not needed when `Security:PublicBaseUrl` is set, which is the recommended configuration. It is honoured from a trusted proxy only when `Security:PublicBaseUrl` is not set.

## What ElPaqueto must be told

1. **Which proxy to trust**: `Security:ReverseProxy:KnownProxies` (addresses) or `Security:ReverseProxy:KnownNetworks` (CIDR networks). The default trusts loopback only, which fits a proxy on the same machine that connects through `127.0.0.1` or `::1`. Forwarded headers from any other address are ignored.
2. **The public URL**: `Security:PublicBaseUrl`, such as `https://nuget.example.test`. The service index and registration responses then always point to that URL.
3. **How many proxies are chained**: `Security:ReverseProxy:ForwardLimit`, 1 by default. Raise it only when several trusted proxies append to `X-Forwarded-For` in turn, and list all of them as trusted.

Keep `Security:Transport:RequireHttps` at its default (`true`).

## Example: proxy in the same Docker Compose project

The proxy and ElPaqueto share a user-defined network with a fixed subnet, so the trusted network is known in advance. Only the proxy publishes a port; ElPaqueto is not reachable from outside the network. The example uses the documentation range `198.51.100.0/24`; choose a private subnet that is free on your Docker host.

```yaml
services:
  proxy:
    image: <your reverse proxy>
    ports:
      - "443:443"
    networks:
      - backend

  elpaqueto:
    image: <the ElPaqueto image>
    environment:
      Security__PublicBaseUrl: "https://nuget.example.test"
      Security__ReverseProxy__KnownNetworks__0: "198.51.100.0/24"
    networks:
      - backend

networks:
  backend:
    ipam:
      config:
        - subnet: 198.51.100.0/24
```

Trust only the network the proxy connects from, and keep it a network that only the proxy and ElPaqueto join: any container on a trusted network can forge the forwarded headers.

## Example: proxy on the Docker host, port published to it

When a proxy on the host forwards to a published container port (`-p 127.0.0.1:5000:8080`), the container sees the connection coming from the Docker bridge gateway, not from loopback. Trust that address, shown as `Gateway` by `docker network inspect bridge` (`192.0.2.1` stands for it below):

```bash
docker run -p 127.0.0.1:5000:8080 \
  -e Security__PublicBaseUrl=https://nuget.example.test \
  -e Security__ReverseProxy__KnownProxies__0=192.0.2.1 \
  <the ElPaqueto image>
```

Publishing on `127.0.0.1` only keeps the plain-HTTP port unreachable from other machines.

## Example: proxy or tunnel agent on the same machine, no container

A proxy or tunnel agent that connects to ElPaqueto through `localhost` needs no trust setting: loopback is trusted by default. Bind ElPaqueto to loopback only and set the public URL:

```bash
export ASPNETCORE_URLS=http://127.0.0.1:5000
export Security__PublicBaseUrl=https://nuget.example.test
```

A tunnel agent behaves like a reverse proxy here: it must set `X-Forwarded-Proto` and `X-Forwarded-For`. If it does not set `X-Forwarded-For`, every request looks local and the HTTPS check cannot tell remote clients apart; prefer an agent that sets it.

## Proxy configurations

Complete, commented configurations are in [`samples/reverse-proxy/`](../../samples/reverse-proxy/). They all serve `https://nuget.example.test` and forward to the Compose service `elpaqueto` on port 8080, as in [`docker-compose.example.yml`](../../docker-compose.example.yml) (see [Running with Docker](docker.md)). The Docker workflow checks their syntax on every pull request (`nginx -t`, `caddy validate`, `docker compose config`); `eng/docker/validate-proxy-samples.sh` runs the same checks locally.

Whatever the proxy, check these six points.

| Topic | What to do |
| --- | --- |
| TLS | Terminate TLS at the proxy. Preferably do not listen on plain HTTP for the feed's host name at all. If you do, redirect **only** a browser's bare `GET /` to HTTPS and answer every other plain-HTTP request with an error, never with a redirect (see [below](#why-plain-http-is-refused-instead-of-redirected)). |
| Forwarded headers | Set `X-Forwarded-Proto` (and `X-Forwarded-Host` if `Security:PublicBaseUrl` is not set) to the real values, **overwriting** what the client sent, and append the client address to `X-Forwarded-For`. Trust only the proxy's address or network in ElPaqueto. |
| Body size | Allow request bodies of at least `MaxPackageSizeGiB` (8 GiB by default), and stream uploads instead of buffering them on the proxy. |
| Timeouts | Allow several minutes for pushes and downloads of large packages. |
| `Authorization` | Pass the `Authorization` and `X-NuGet-ApiKey` headers through unchanged. Do not add proxy-level authentication (basic auth, forward auth) on the same host name: the client cannot answer two Basic challenges. |
| Path prefix | To serve the feed under a path (`https://nuget.example.test/feed/`), set `PathBase=/feed` in ElPaqueto and forward the path unchanged, prefix included. `X-Forwarded-Prefix` is not supported. |

When the forwarded headers are wrong, the service index shows it. With `Security:PublicBaseUrl` set, its URLs are always right, but requests through the proxy get `403 HTTPS is required.` if the proxy is not trusted or does not send `X-Forwarded-Proto: https`. Without `Security:PublicBaseUrl`, a proxy that does not set the headers yields URLs such as `http://elpaqueto:8080/v3/...`, which clients cannot reach. Check with:

```bash
curl -s -u <user>:<password> https://nuget.example.test/v3/index.json   # every "@id" starts with https://nuget.example.test/
```

### nginx

[`samples/reverse-proxy/nginx/elpaqueto.conf`](../../samples/reverse-proxy/nginx/elpaqueto.conf), for the `http {}` context (`/etc/nginx/conf.d/` in the official image).

- TLS: `listen 443 ssl` with your certificate. The optional port-80 server redirects `GET /` only and returns `404` for everything else.
- Forwarded headers: `proxy_set_header X-Forwarded-Proto https` and `X-Forwarded-Host $host` replace the client's values; `X-Forwarded-For $proxy_add_x_forwarded_for` appends the client address, and ElPaqueto reads the entry nginx added (`Security:ReverseProxy:ForwardLimit` 1).
- Body size: nginx refuses bodies over 1 MiB by default (`413`). The sample sets `client_max_body_size 8g` and `proxy_request_buffering off`, so uploads are streamed to ElPaqueto instead of being written to nginx's disk first.
- Timeouts: `client_body_timeout`, `proxy_send_timeout` and `proxy_read_timeout` are set to 300 s. They limit the time **between** two reads or writes, not the whole transfer.
- `Authorization`: passed through by default. No `auth_basic` on this server.

### Caddy

[`samples/reverse-proxy/caddy/Caddyfile`](../../samples/reverse-proxy/caddy/Caddyfile).

- TLS: automatic (ACME) for a public host name. Caddy redirects every plain-HTTP request to HTTPS by default; the sample turns that off with `auto_https disable_redirects` and adds an `http://` site that redirects `GET /` only and answers `404` to everything else.
- Forwarded headers: `reverse_proxy` sets `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` itself and does not keep the client's values unless the client is listed in Caddy's `trusted_proxies`. Do not override them with `header_up`.
- Body size: `request_body { max_size 8GiB }` makes the limit explicit (use `GiB`: Caddy reads `GB` as decimal, 8 GB is below 8 GiB). Uploads are streamed.
- Timeouts: if you set `servers { timeouts { read_body ... } }`, it limits the reading of the **whole** body, so it must cover the slowest push of the largest package.
- `Authorization`: passed through. No `basic_auth` in this site.

### Traefik

[`samples/reverse-proxy/traefik/compose.yml`](../../samples/reverse-proxy/traefik/compose.yml): Traefik v3 and ElPaqueto in one Compose project, routed with container labels.

- TLS: only a `websecure` entry point on port 443; no plain-HTTP entry point, hence no redirect. Add a certificate resolver (ACME) for your host name; without one, Traefik serves its self-signed default certificate.
- Forwarded headers: Traefik sets `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` and does not trust the client's values unless the entry point's `forwardedHeaders.trustedIPs` lists the client. Leave that option unset when Traefik faces the Internet.
- Body size: Traefik sets no request body limit unless the `buffering` middleware is used. Do not add it: it would also hold whole uploads in memory or on disk.
- Timeouts: **Traefik v3 reads the whole request within 60 seconds by default** (`respondingTimeouts.readTimeout`, checked with Traefik 3.7.18), which cuts long pushes. The sample raises it to 600 s on the `websecure` entry point.
- `Authorization`: passed through. No `basicAuth` or `forwardAuth` middleware on this router.
- The Docker provider reads the labels through the Docker socket. Mounting it `:ro` does **not** limit the Docker API: whoever controls Traefik controls the Docker daemon, which is root on the host. Keep the Traefik API and dashboard disabled, as in the sample, or put a Docker socket proxy that only allows reading containers in between, or use the file provider instead of labels.

### Tunnels

A TLS-terminating tunnel (a tunnel agent next to the server, connected to a provider's edge) is a reverse proxy for ElPaqueto, with the same six points:

- trust only the address the agent connects from: loopback when the agent runs on the same machine (the default), otherwise the agent's container address or network;
- the agent must set `X-Forwarded-Proto: https` and `X-Forwarded-For` (see [above](#example-proxy-or-tunnel-agent-on-the-same-machine-no-container));
- check the provider's own limits on request body size and request duration against your largest package;
- do not enable the provider's access control on the feed's host name if it answers with its own login or challenge: NuGet clients cannot complete it.

## Client configuration

A `NuGet.Config` for the feed, without any secret in it:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="elpaqueto" value="https://nuget.example.test/v3/index.json" />
  </packageSources>
</configuration>
```

Credentials stay out of that file. In CI, give them through the `NuGetPackageSourceCredentials_<source name>` environment variable, read by `dotnet restore` and `dotnet nuget push`, from the CI system's secret store:

```bash
export NuGetPackageSourceCredentials_elpaqueto="Username=<user>;Password=<password>"
dotnet restore
```

On a workstation, put them in the user-level `NuGet.Config` (`packageSourceCredentials`) or use a credential provider, never in the repository's file.

## Checking the setup

From a remote machine:

```bash
# Through the proxy: 200, and every URL starts with the public URL.
curl -s -u <user>:<password> https://nuget.example.test/v3/index.json

# Straight to the server over HTTP, if it is reachable at all: 403 "HTTPS is required."
curl -s -i http://<server address>:8080/v3/index.json
```

If requests through the proxy get `403 HTTPS is required.`, the proxy is not trusted or does not send `X-Forwarded-Proto: https`. The server log then contains, once, a warning naming the address whose forwarded headers were ignored.

## Why plain HTTP is refused instead of redirected

A redirect from `http://` to `https://` arrives after the client has already sent its request, including its `Authorization` header or API key, in clear text. Many clients then repeat the request, credentials included, to the new location. A `403` with no `WWW-Authenticate` header ends the exchange, and never asks the client for credentials over plain HTTP. See [the technical note](../technical/forwarded-headers.md) for the threats this setup addresses.
