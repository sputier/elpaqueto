# Running behind a reverse proxy or tunnel

ElPaqueto does not terminate TLS itself in the recommended setup. It listens on plain HTTP (port 8080 in the container), and a reverse proxy or tunnel in front of it terminates TLS and forwards the requests. This page describes what the server expects from that proxy. The settings are described in the [security configuration reference](configuration.md).

## What the proxy must do

1. **Terminate TLS** and forward the request to ElPaqueto over HTTP.
2. **Set `X-Forwarded-Proto: https`.** Without it, a request from a remote client counts as plain HTTP and is rejected with `403 HTTPS is required.`
3. **Set `X-Forwarded-For`** to the client address, appending to any value the client sent. Most proxies do this by default. Without it, every request seems to come from the proxy itself.
4. **Never redirect to HTTP or downgrade.** Redirecting HTTP to HTTPS on the public side is the proxy's choice; ElPaqueto itself never redirects.

`X-Forwarded-Host` is not needed when `Security:PublicBaseUrl` is set, which is the recommended configuration. It is honoured from a trusted proxy only when `Security:PublicBaseUrl` is not set.

## What ElPaqueto must be told

1. **Which proxy to trust**: `Security:ReverseProxy:KnownProxies` (addresses) or `Security:ReverseProxy:KnownNetworks` (CIDR networks). The default trusts loopback only, which fits a proxy on the same machine that connects through `127.0.0.1` or `::1`. Forwarded headers from any other address are ignored.
2. **The public URL**: `Security:PublicBaseUrl`, such as `https://nuget.example.test`. The service index and registration responses then always point to that URL.
3. **How many proxies are chained**: `Security:ReverseProxy:ForwardLimit`, 1 by default. Raise it only when several trusted proxies append to `X-Forwarded-For` in turn, and list all of them as trusted.

Keep `Security:Transport:RequireHttps` at its default (`true`).

## Response headers

ElPaqueto sets `X-Content-Type-Options`, `Referrer-Policy`, `X-Frame-Options`, `Content-Security-Policy` and, for pages and errors, `Cache-Control` itself. Do not add them again in the proxy: two `Content-Security-Policy` headers both apply, so a second, stricter one breaks the web UI, and duplicated single-value headers are ambiguous. `Strict-Transport-Security` is off in ElPaqueto by default (`Security:Hsts`): send it from the proxy **or** enable it in ElPaqueto, not both. Do not let the proxy cache responses to requests that carry `Authorization` or `X-NuGet-ApiKey`.

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
