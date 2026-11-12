# Security configuration

This page documents the settings of ElPaqueto's security module. Settings that come from the inherited server (database, storage, search, mirror, ...) are described in the [server configuration reference](../docs/configuration.md).

## Sources and precedence

Settings are read from, in increasing order of precedence:

1. `appsettings.json` and `appsettings.<Environment>.json`
2. environment variables, with `__` as the section separator (`Security__Database__ConnectionString`)
3. command-line arguments
4. files in `/run/secrets` (Docker secrets), where each file is named after the setting with `__` as the separator and contains the value

A secret file therefore overrides the same setting from an environment variable. Put anything sensitive in a secret file or an environment variable, never in `appsettings.json` under version control.

## Validation

The server and every command-line command validate the security settings before doing anything. An invalid setting stops the process with a non-zero exit code. The log names the setting and the reason, and never prints the value:

```text
Invalid 'Security' configs: Security:Database:ConnectionString is required and must not be empty.
```

## Settings

### `Security:Database`

| Setting | Default | Description |
| --- | --- | --- |
| `Security:Database:ConnectionString` | `Data Source=auth.db` | SQLite connection string of the database that stores users, permissions and API keys. Required, at most 1024 characters. A relative path is resolved against the working directory. |

Examples:

```bash
# Environment variable
export Security__Database__ConnectionString="Data Source=/data/db/auth.db"

# Docker secret file (the file name is the setting name, with __ as separator)
printf '%s' 'Data Source=/data/db/auth.db' > /run/secrets/Security__Database__ConnectionString
```

### `Security:Passwords`

| Setting | Default | Description |
| --- | --- | --- |
| `Security:Passwords:IterationCount` | `210000` | PBKDF2-HMAC-SHA512 iterations for new password hashes. Between 100000 and 10000000. Hashes made with a lower count still verify and are re-hashed after a successful login. |
| `Security:Passwords:MinimumLength` | `15` | Minimum password length, counted in characters after Unicode normalization. Between 12 and 256. |

The maximum password length is fixed at 256 characters. The policy has no composition rules (no required digits or symbols): long passphrases are preferred. A password equal to the user name (ignoring case) is rejected. The policy applies when a password is set, never at login.

### `Security:Authentication`

| Setting | Default | Description |
| --- | --- | --- |
| `Security:Authentication:VerificationCacheTtl` | `00:05:00` | How long a successful password verification is remembered in memory, as a time span up to `01:00:00`. `00:00:00` disables the cache. A disabled user, changed permissions and a changed password always apply to the next request: the cache only keeps an unchanged, valid password cheap. |

**Upgrading from BaGetter:** the inherited settings `Authentication:Credentials` (plain-text users) are no longer used: users are stored, hashed, in the security database, and a request that carries credentials only authenticates if the user exists there. The legacy `ApiKey` and `Authentication:ApiKeys` settings are not retired yet: they still gate publishing, and publishing is open to everyone while neither is set. Until deny-by-default authorization and API keys are released, **do not expose the server to untrusted networks**.

### `Security:ApiKeys`

| Setting | Default | Description |
| --- | --- | --- |
| `Security:ApiKeys:LastUsedUpdateInterval` | `00:05:00` | Minimum time between two writes of an API key's "last used" timestamp, as `[d.]hh:mm:ss`. Between `00:00:01` and `1.00:00:00`. The timestamp is accurate to within this value; a shorter interval means more writes to the security database. |

API keys are stored hashed: the complete key (`epq_...`) is shown once, when it is created, and cannot be displayed again. A lost key is revoked and replaced. Passwords that have the format of an API key are refused.

### `Security:ReverseProxy`

Which reverse proxies are trusted to report the client's address, the original scheme and the original host. See [Running behind a reverse proxy or tunnel](reverse-proxy.md) for complete examples.

| Setting | Default | Description |
| --- | --- | --- |
| `Security:ReverseProxy:KnownProxies` | empty | List of proxy IP addresses, such as `192.0.2.10` or `2001:db8::10`. |
| `Security:ReverseProxy:KnownNetworks` | empty | List of proxy networks in CIDR notation, such as `198.51.100.0/24`. The address must not have bits set beyond the prefix (`198.51.100.1/24` is rejected). |
| `Security:ReverseProxy:ForwardLimit` | `1` | Number of proxies in front of the server: how many entries of `X-Forwarded-For` and `X-Forwarded-Proto` are processed, starting from the right. Between 1 and 10. |

When neither list is set, only loopback (`127.0.0.0/8` and `::1`) is trusted. Setting either list **replaces** that default: add `127.0.0.1` and `::1` yourself if a proxy on the same machine also connects. There is no setting that trusts every address, and networks that cover every address (`0.0.0.0/0`, `::/0`, or an IPv6 network containing all IPv4-mapped addresses such as `::ffff:0:0/96`) are rejected.

Addresses must be written in full: four decimal octets for IPv4 (`127.1` or hexadecimal forms are rejected), IPv6 without a zone id (`%eth0`) and not in IPv4-mapped form (`::ffff:192.0.2.10`; write `192.0.2.10`, which also matches clients of a dual-stack listener).

Lists are set with one key per entry:

```bash
export Security__ReverseProxy__KnownNetworks__0="198.51.100.0/24"
export Security__ReverseProxy__KnownProxies__0="192.0.2.10"
```

Entries are merged by index across configuration sources: an environment variable `..__0` replaces entry 0 of `appsettings.json` but keeps its entry 1. Set the whole list in a single source so that no stale proxy stays trusted.

Setting the list itself as a single value (`Security__ReverseProxy__KnownNetworks=198.51.100.0/24`) is rejected at startup, because it would otherwise be silently ignored.

Forwarded headers sent by any other address are ignored: the request keeps the scheme, host and client address of its connection. The headers processed are `X-Forwarded-For`, `X-Forwarded-Proto` and, only when `Security:PublicBaseUrl` is not set, `X-Forwarded-Host`. `X-Forwarded-Prefix` is not supported: use `PathBase`.

Why the default is loopback only: trusting a whole private range by default would let any host on a shared private network (another container, another tenant on the same network) forge its address and the scheme. A server behind a proxy on another host or in another container must list that proxy. Until it does, remote requests are rejected with `403 HTTPS is required.` (see `Security:Transport`), and the server logs once that it ignored forwarded headers from an untrusted address.

### `Security:PublicBaseUrl`

| Setting | Default | Description |
| --- | --- | --- |
| `Security:PublicBaseUrl` | not set | The URL clients use to reach the server, such as `https://nuget.example.test`. Optional; an empty value means not set. |

NuGet clients follow the absolute URLs listed in the service index (`/v3/index.json`) and in registration responses. When `Security:PublicBaseUrl` is set, every such URL uses its scheme and host, whatever the request's `Host` or `X-Forwarded-Host` header says. The path prefix still comes from `PathBase`: with `PathBase=/feed`, the service index is `https://nuget.example.test/feed/v3/index.json`.

The value must be an absolute `https` URL without path, query, fragment or user information. An invalid value stops the server at startup.

When it is not set, URLs are built from the request's `Host` header (or `X-Forwarded-Host` from a trusted proxy), and the server logs a warning at startup if it listens on anything other than loopback. Set it for every deployment that is reachable from other machines.

If you cannot set it, restrict the accepted `Host` header with ASP.NET Core's built-in host filtering, the top-level `AllowedHosts` setting (a `;`-separated list, such as `AllowedHosts=nuget.example.test`): a request with another `Host` gets `400`. This filter checks the `Host` header the server receives, before forwarded headers are applied.

### `Security:Transport`

| Setting | Default | Description |
| --- | --- | --- |
| `Security:Transport:RequireHttps` | `true` | Requests from clients that are not on the same machine must use HTTPS. |

The scheme and the client address are the ones left after trusted forwarded headers: a request that reaches the server through a trusted proxy counts as HTTPS when the proxy reports `X-Forwarded-Proto: https`, and counts as remote when the proxy reports a remote `X-Forwarded-For`.

A remote plain-HTTP request is answered with status `403` and the plain-text body `HTTPS is required.`, before any authentication, whatever its route and whether or not it carries credentials (`Authorization` or `X-NuGet-ApiKey`). The answer has no `WWW-Authenticate` header, so clients are never invited to send credentials over plain HTTP, and it is never a redirect: a client that already sent credentials over HTTP would send them again.

Requests from loopback (`127.0.0.0/8`, `::1`) and over non-IP transports (Unix sockets, named pipes) are allowed over plain HTTP, for local development, the container health check and local tests. Note that with Docker port publishing, a request from the Docker host reaches the container from the bridge gateway address, not from loopback.

Health probes from other machines (an orchestrator, a load balancer, an uptime monitor) are remote requests too: send them through the HTTPS endpoint or the reverse proxy, or run them from inside the container or host, otherwise they get `403`.

`false` disables the check and logs a warning at startup. Use it only on an isolated test network.

More settings are added to this page as the features that need them are released.
