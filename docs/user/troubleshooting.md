# Troubleshooting

Start with the server log (`docker logs elpaqueto`). Security decisions are logged under the category `ElPaqueto.Security.Audit` with an event id and a reason, never with a password or a key: see [Logging and monitoring](logging.md). Clients only see the status code, on purpose.

When you share a log or a configuration in an issue, remove host names, user names and anything that looks like a secret first. Never post a password, an API key (`epq_...`) or an `Authorization` header. Report security problems privately, as described in the [security policy](../../SECURITY.md).

## `401 Unauthorized`

The request carried no usable credentials. The answer is the same whatever the cause; the log (events 1002 and 1003) has the reason:

| Reason in the log | Cause | Fix |
| --- | --- | --- |
| no event | No credentials were sent. | Configure the source credentials ([client setup](client-setup.md)). NuGet sends them only after a `401` challenge for that source: check the source name in `NuGetPackageSourceCredentials_<name>` or `packageSourceCredentials`. |
| `unknown_user` | The user name does not exist (names ignore case). | `admin user list --include-disabled`. |
| `bad_password` | Wrong password. | `admin user reset-password <name>`. |
| `disabled` | The user is disabled. | `admin user enable <name>`. |
| `revoked`, `expired`, `owner_disabled`, `wrong_secret`, `unknown_key` | The API key cannot be used. | `admin key list --user <name> --include-revoked`; create a new key. |
| `malformed`, `oversized` (logged at `Debug` only, unless the value has the key format) | The value is not a well-formed key or Basic header, for example with a trailing space or line break. | Check how the value is pasted into the secret store. |

`X-NuGet-ApiKey` wins over Basic credentials: a request that sends an invalid key in that header fails even with a valid password.

## `403 Forbidden`

- **Body `HTTPS is required.`**: a plain-HTTP request from another machine. Use the `https://` URL. Behind a reverse proxy, the proxy is not trusted (`Security:ReverseProxy`) or does not send `X-Forwarded-Proto: https`; the log shows event 1202 once with the ignored address. See [Running behind a reverse proxy](reverse-proxy.md#checking-the-setup).
- **Empty body**: the credentials are valid but lack a permission (event 1101 names the route and what is missing):
  - pushing, deleting or relisting needs a key with `Publish` in `--api-key`. A password, or a key used as the source password, is refused for writes even with `Publish`;
  - a hard delete (`PackageDeletionBehavior: HardDelete`) also needs `Admin`;
  - reading needs `Read`: `Publish` and `Admin` do not include it;
  - a key never has more than its owner currently holds: check `effectivePermissions` in `admin key show <keyId>`.

## `429 Too Many Requests`

Too many failed sign-ins from your address, or for your user name from your address. Nothing is checked while it lasts, even correct credentials. Wait for the number of seconds in `Retry-After`, then **fix the credentials before retrying**. Retrying the same wrong password is counted once and leads to `401`, not `429`; `429` comes from several *different* wrong passwords or user names from your address (for example several stale `NuGet.Config` files or CI secrets). Clients behind one NAT or an untrusted proxy share one budget. Details: [`Security:Throttling`](configuration.md#securitythrottling).

## `503 Service Unavailable` during sign-in

The server is already running as many password checks as it allows (`Security:Throttling:MaxConcurrentVerifications`). It is transient; if it persists under normal load, raise that limit or `VerificationQueueLimit`. API keys do not need a password check: prefer them for busy CI.

## `409 Conflict` on push

The version already exists and `AllowPackageOverwrites` is `false`. Publish a new version, or add `--skip-duplicate` in CI. See [publishing](publishing.md#pushing-a-version-that-already-exists).

## Wrong URLs in the service index

The client reads `/v3/index.json`, then follows the absolute URLs in it. If they point to an internal host name, to `http://`, or to the wrong port, set `Security:PublicBaseUrl` to the URL clients use (for example `https://nuget.example.test`). Without it, URLs come from the request's `Host` header, or `X-Forwarded-Host` from a trusted proxy. A path prefix comes from `PathBase`. The startup log warns (event 1902) when `Security:PublicBaseUrl` is not set and the server listens beyond loopback.

## NuGet errors

| Code | Meaning |
| --- | --- |
| `NU1301` with `401` | Restore was refused: see [`401`](#401-unauthorized). Also clear the HTTP cache (`dotnet nuget locals http-cache --clear`) after fixing the credentials. |
| `NU1301` with `403` | See [`403`](#403-forbidden). |
| `NU1301` with a connection error | The URL, the port, DNS or TLS is wrong, or the server is down: check `curl -sS -o /dev/null -w '%{http_code}' https://nuget.example.test/health`, which needs no credentials and answers `200`. |
| `NU1302`, `NU1803` | The source is `http://`. Use `https://`. `allowInsecureConnections` is only for a server on your own machine. |

## Reading the logs without exposing secrets

- The log never contains passwords, password hashes, `Authorization` or `X-NuGet-ApiKey` values, API key secrets or connection strings, at any level. API keys are identified by their public key id.
- A user name that does not exist is logged as `<unknown:xxxxxxxx>`, because it could be a password typed into the user name field.
- Do not run the server with `ASPNETCORE_ENVIRONMENT=Development` to get more detail: its error page shows request headers, credentials included. Raise the log level instead, for example `Logging__Console__LogLevel__ElPaqueto.Security.Audit=Debug`.

## The server does not start

| Message | Fix |
| --- | --- |
| `Invalid 'Security' configs: ...` | The named setting is invalid: see [configuration](configuration.md). |
| `The setting '...' comes from BaGetter and is no longer supported` | Remove `ApiKey` or `Authentication:*`: see [upgrading from BaGetter](upgrading-from-bagetter.md). |
| `ElPaqueto could not load its configuration: Access to the path '/run/secrets/...' is denied.` | Give the secret file to UID 1654 with mode `0400`: see [Docker](docker.md#default-settings). |
| `Access to the path '/data/db' is denied` | A bind-mounted folder must belong to UID 1654: see [volumes and file ownership](docker.md#volumes-and-file-ownership). |
