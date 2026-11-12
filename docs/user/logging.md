# Logging and monitoring

ElPaqueto logs with the standard .NET logging providers, configured in the `Logging` section (`appsettings.json`, environment variables such as `Logging__Console__LogLevel__Default`, or files in `/run/secrets`, see [configuration](configuration.md#sources-and-precedence)). Its security decisions form an **audit trail** under one category, `ElPaqueto.Security.Audit`, that you can route and keep separately. The complete list of events, with their ids and properties, is in [Audit logging](../technical/audit-logging.md).

## What you get by default

The shipped `appsettings.json` writes to the console:

- `Warning` and above for everything;
- `Information` and above for `Microsoft.Hosting.Lifetime` (startup and shutdown) and for the audit trail `ElPaqueto.Security.Audit`;
- every line to **standard error** (`LogToStandardErrorThreshold: Trace`), so that the standard output of the `admin` commands (JSON listings, API key tokens) stays machine-readable. A container runtime or a service manager collects both streams.

With these settings you see failed sign-ins, refused API keys, authorization denials, refused plain-HTTP requests, throttling trips, startup security warnings and every change made with the `admin` commands. Successful sign-ins are logged at `Debug` only: NuGet clients authenticate every request, so one line per success would flood the log. To see them while investigating:

```bash
Logging__Console__LogLevel__ElPaqueto.Security.Audit=Debug
```

To keep the audit trail out of the console, set the same variable to `Warning` (failures only) or `None`.

Passwords, password hashes, `Authorization` and `X-NuGet-ApiKey` header values, API key tokens, connection strings and request bodies are never logged, at any level. A user name is logged in clear only when it names an existing account. Any other value (it may be a password or an API key typed in the user name field, or contain line breaks) is logged as `<unknown:xxxxxxxx>`, a fingerprint that is the same for the same value until the server restarts: you see that one value is retried, not what it is.

## What to monitor and alert on

Filter on the category `ElPaqueto.Security.Audit` and the event id (or name). Suggested alerts:

| Signal | Events | What it usually means |
| --- | --- | --- |
| A burst of failed sign-ins from one address, or for one user name (or one `<unknown:...>` fingerprint) from several addresses | 1002 `AuthenticationFailed` (`unknown_user`, `bad_password`) | Password guessing, or a stale password in a NuGet configuration or credential provider. |
| Throttling trips | 1801 `ThrottleTripped` | The same, past the throttling limits. Repeated trips for the same `Client` deserve a firewall rule. |
| Refused API keys with a key id | 1003 `ApiKeyRejected` (`revoked`, `expired`, `owner_disabled`, `wrong_secret`) | A CI pipeline still uses an old key, or someone tries a leaked key id. |
| Authorization denials | 1101 `AuthorizationDenied` | An account or key used beyond its permissions: misconfiguration, or a compromised low-privilege credential. |
| Refused plain-HTTP requests (Warning at most once a minute, the others at Debug) | 1201 `TransportRejected`, 1202 `UntrustedForwardedHeaders` | A client or a reverse proxy is misconfigured (see [Running behind a reverse proxy](reverse-proxy.md)). |
| Any startup security warning | 1901–1906 | The server runs with a weakened setting: check it was intended. |
| Any user or key change | 2001–2009, 2101–2103 | Compare with your change records: an unexpected change means someone ran the `admin` commands. |
| Failed admin commands | 2099 `AdminCommandFailed` | The security database could not be used. |

`AuthenticationFailed` and `ApiKeyRejected` for malformed values are `Debug`: anyone can send them, and they are refused before any check.

## Structured output

For a log collector, use the JSON console formatter: each line is a JSON object with the `EventId`, `LogLevel`, `Category`, `Message` and every property in `State`.

```bash
Logging__Console__FormatterName=json
Logging__Console__FormatterOptions__IncludeScopes=true
```

Then filter on `"Category":"ElPaqueto.Security.Audit"` and on `EventId`.

## Shipping logs from Docker

The container writes every log line to standard error; Docker captures it with its logging driver:

- `docker logs <container>` shows it, and so does `docker compose logs elpaqueto`.
- The default `json-file` driver keeps logs on the host without a size limit: set one, for example in Compose:

  ```yaml
  services:
    elpaqueto:
      logging:
        driver: json-file
        options:
          max-size: "10m"
          max-file: "5"
  ```

- To send logs to a collector, use another driver (`journald`, `syslog`, `fluentd`, `gelf`, ...) with `--log-driver` or the Compose `logging` key, or let an agent on the host read the `json-file` logs. With the JSON console formatter above, the collector receives one JSON object per line.

Outside containers, a service manager collects standard error as well (for example the systemd journal). On Windows, `Host.CreateDefaultBuilder` also registers the Event Log provider, at `Warning` by default; `Logging__EventLog__LogLevel__ElPaqueto.Security.Audit=Information` sends the audit trail there too.

## Personal data

The audit trail contains user names and client IP addresses, which can be personal data. Keep these logs under the same access and retention rules as your web server's access logs, and set the retention of your log collector accordingly. Passwords and keys are never in them.

## Never expose a `Development` instance

In the `Development` environment, error pages show the request, including its `Authorization` header, to whoever causes the error. The server logs a warning (event 1904) when it starts in `Development`. Run exposed instances in `Production` (the default when no environment is set).

Do not add HTTP request logging middleware to ElPaqueto without reading [Audit logging](../technical/audit-logging.md#what-is-never-logged): request headers and bodies carry credentials and packages.
