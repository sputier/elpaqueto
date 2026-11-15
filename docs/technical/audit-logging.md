# Audit logging and secret redaction

The security module records its decisions as structured log events under one logging category, `ElPaqueto.Security.Audit`, so that operators can filter, route and retain them apart from the rest of the server's logs. There is no audit table: events go to the configured logging providers only (see [Logging and monitoring](../user/logging.md) for the operator's side).

## Design

- **One category.** Audit events are written through an `ILogger<Audit>` (`src/ElPaqueto.Security/Audit.cs`, a marker type whose full name is the category) or through a logger created with `AuditLog.Category`. Every audit event, whatever class raises it, has that category.
- **One catalogue.** Events are declared with the `LoggerMessage` source generator in `src/ElPaqueto.Security/Auditing/AuditLog.cs`, except the user and API key administration events of the `admin` commands, which stay in `src/ElPaqueto.Security/Users/UserAdministrationLog.cs` (same category, block 2000–2099). New security events are added to `AuditLog`, never as ad-hoc log calls.
- **Fixed ids and names.** Every `LoggerMessage` event of the security module, audit or not, has an explicit `EventId` and `EventName`. Ids are allocated by block (below) and never reused. `AuditLogCatalogueTests` fails when two events share an id or a name, when an event has no explicit id, when an audit event leaves the 1000–2999 range, or when an event has no row in the tables of this page.
- **Safe values only.** Properties are ids (user GUIDs, public API key ids), fixed vocabularies (reasons, methods, scopes), client addresses, server-defined route names and the user names of existing accounts. `AuditValues` (`src/ElPaqueto.Security/Auditing/AuditValues.cs`) converts request data into such values.
- **No flooding.** Successful sign-ins are `Debug` (NuGet clients authenticate every request). Failures that anyone can send without limit (malformed headers, values without the API key format) are `Debug`; failures that are counted by throttling are `Warning`, so their number is bounded per client address (a distributed attack from many addresses still produces one line per attempt, which is what an operator wants to see). Refused plain-HTTP requests, which are anonymous and not throttled, are `Warning` at most once a minute and `Debug` otherwise. A throttle trip is logged once; the refused attempts behind it are `Debug`.

### Event id allocation

| Block | Area | Category |
| --- | --- | --- |
| 1000–1099 | Authentication | `ElPaqueto.Security.Audit` |
| 1100–1199 | Authorization | `ElPaqueto.Security.Audit` |
| 1200–1299 | Transport (HTTPS policy, forwarded headers) | `ElPaqueto.Security.Audit` |
| 1300–1799 | Reserved for future audit events | `ElPaqueto.Security.Audit` |
| 1800–1899 | Sign-in throttling | `ElPaqueto.Security.Audit` |
| 1900–1999 | Startup security warnings | `ElPaqueto.Security.Audit` |
| 2000–2099 | User and API key administration (`admin` commands) | `ElPaqueto.Security.Audit` |
| 2100–2999 | Reserved for future audit events | `ElPaqueto.Security.Audit` |
| 3000–3099 | Operational events of authentication and API keys | the class's own category |
| 3100–3199 | Security configuration validation | `ElPaqueto.Security.Configuration` |
| 3200–3299 | Security database | `ElPaqueto.Security.Data.AuthDatabaseMigrator` |

Inherited code (`ElPaqueto.Core`, `ElPaqueto.Web`, storage and database providers) logs without event ids, under its own categories. Framework categories (`Microsoft.*`) have their own ids; they never use the audit category.

## Audit events

Common properties: `ClientAddress` is the connection's remote address **after trusted forwarded-header processing** (the client behind a trusted proxy, never the proxy itself; IPv4-mapped IPv6 addresses are written as IPv4), or `-` when the transport has none. `-` also stands for any property that does not apply.

### Authentication (1000–1099)

| Id | Name | Level | Properties | Emitted when |
| --- | --- | --- | --- | --- |
| 1001 | `AuthenticationSucceeded` | Debug | `UserId`, `Method` (`password`, `api_key`), `KeyId`, `ClientAddress` | A password or an API key is accepted. |
| 1002 | `AuthenticationFailed` | Warning; Debug for `malformed` and for a [repetition](throttling.md#repeated-identical-attempts) of a failure already counted | `Method` (`password`), `Reason`, `UserName` (see [user names](#user-names-in-logs)), `ClientAddress` | A Basic header with a password (not an API key) is refused. |
| 1003 | `ApiKeyRejected` | Warning when the value has the token format, the first time from a client in the throttling window; Debug otherwise | `Reason`, `KeyId`, `CredentialSource` (`x-nuget-apikey`, `basic-password`), `ClientAddress` | An API key is refused, from the header or as a Basic password. |
| 1004 | `AuthenticationThrottled` | Debug | `Method`, `StatusCode` (429 or 503), `ClientAddress`, `RetryAfterSeconds` | An attempt is refused by throttling or for lack of verification capacity, without checking its credential. |

`AuthenticationFailed` reasons: `malformed` (the header cannot be decoded into a user name and a password: refused before throttling, hence Debug), `unknown_user` (no such user, including invalid user names), `disabled` (the user exists but is disabled), `bad_password` (active user, wrong password).

`ApiKeyRejected` reasons: `oversized`, `malformed` (also: two `X-NuGet-ApiKey` values), `unknown_key`, `wrong_secret`, `revoked`, `expired`, `owner_disabled`. `KeyId` is the public key id parsed from a value with the token format, never the secret; `-` for a value without the format.

### Authorization (1100–1199)

| Id | Name | Level | Properties | Emitted when |
| --- | --- | --- | --- | --- |
| 1101 | `AuthorizationDenied` | Warning | `UserId`, `Method`, `KeyId`, `HttpMethod`, `Endpoint`, `Reason`, `RequiredPermission`, `ClientAddress` | An **authenticated** caller is denied an endpoint (403). |

Reasons: `unmapped_endpoint`, `route_mismatch`, `method_not_allowed`, `api_key_header_required` (a write without an API key in `X-NuGet-ApiKey`), `missing_permission`. `Endpoint` is the key of the permission table (a route name, an endpoint name or `page:/...`), never the request path. `HttpMethod` is one of the standard methods or `OTHER`. Anonymous requests that are challenged (401) are not logged: every NuGet client starts with one.

### Transport (1200–1299)

| Id | Name | Level | Properties | Emitted when |
| --- | --- | --- | --- | --- |
| 1201 | `TransportRejected` | Warning at most once a minute; Debug otherwise | `Scheme` (`http`, `other`), `ClientAddress` | A remote plain-HTTP request is refused with 403 while HTTPS is required. Its credentials are never read. |
| 1202 | `UntrustedForwardedHeaders` | Warning | `ClientAddress` | A refused plain-HTTP request carried forwarded headers that were not applied. Once per process. |

### Throttling (1800–1899)

| Id | Name | Level | Properties | Emitted when |
| --- | --- | --- | --- | --- |
| 1801 | `ThrottleTripped` | Warning | `Scope` (`Client`, `Network`, `Pair`, `Account`, `Repeat`), `Client`, `Seconds` | A failure counter trips. Once per trip, never per refused attempt. `Client` is the IPv4 address, the IPv6 /64, or the IPv6 /48 for `Network`; never the user name. |

### Startup security warnings (1900–1999)

| Id | Name | Level | Properties | Emitted when |
| --- | --- | --- | --- | --- |
| 1901 | `HttpsNotRequired` | Warning | — | `Security:Transport:RequireHttps` is `false`. |
| 1902 | `PublicBaseUrlMissing` | Warning | — | `Security:PublicBaseUrl` is not set and the server listens beyond loopback. |
| 1903 | `EndpointNotMapped` | Warning | `Endpoint` | An endpoint has no row in the permission table: it is denied to everyone. |
| 1904 | `DevelopmentEnvironment` | Warning | — | The server runs in the `Development` environment. |
| 1905 | `DatabasePermissionsNarrowed` | Warning | `Path` | The security database file was readable or writable by other users and was narrowed (Unix). |
| 1906 | `DatabasePermissionsNotNarrowed` | Warning | — | The permissions of the security database could not be narrowed (Unix). |

### User and API key administration (2000–2099)

Raised by the `admin` commands (origin: the command line; there is no administration over HTTP). User names come from the security database or passed the user name rules. API keys are named by their public key id only.

| Id | Name | Level | Properties | Emitted when |
| --- | --- | --- | --- | --- |
| 2001 | `AdministratorBootstrapped` | Information | `UserName`, `UserId` | `admin bootstrap` created the first administrator. |
| 2002 | `BootstrapRefused` | Information | `UserName`, `Reason` | `admin bootstrap` was refused (an administrator exists, or the name is taken). |
| 2003 | `UserCreated` | Information | `UserName`, `UserId`, `Permissions` | `admin user create`. |
| 2004 | `UserDisabled` | Information | `UserName`, `UserId` | `admin user disable`. |
| 2005 | `UserEnabled` | Information | `UserName`, `UserId` | `admin user enable`. |
| 2006 | `UserPermissionsChanged` | Information | `UserName`, `UserId`, `OldPermissions`, `NewPermissions` | `admin user set-permissions`. |
| 2007 | `UserPasswordReset` | Information | `UserName`, `UserId` | `admin user reset-password`. |
| 2008 | `LastActiveAdministratorProtected` | Information | `UserName` | A change was refused because it would leave no active administrator. |
| 2009 | `ConcurrentChangeRejected` | Warning | `UserName` | A change was refused because the user changed concurrently. |
| 2010 | `ApiKeyCreated` | Information | `KeyId`, `UserName`, `UserId`, `Permissions`, `ExpiresAt` | `admin key create`. The token is never logged. |
| 2011 | `ApiKeyRevoked` | Information | `KeyId`, `UserName`, `UserId` | `admin key revoke`. |
| 2012 | `ApiKeyRotated` | Information | `OldKeyId`, `NewKeyId`, `UserName`, `UserId`, `ExpiresAt` | `admin key rotate`. |
| 2099 | `AdminCommandFailed` | Error | `Command`, exception | An `admin` command failed unexpectedly. |

## Operational events (3000–3299)

Not part of the audit trail; listed so that the allocation is complete.

| Id | Name | Level | Category | Meaning |
| --- | --- | --- | --- | --- |
| 3001 | `PasswordRehashNotStored` | Warning | `ElPaqueto.Security.Authentication.ElPaquetoAuthenticationHandler` | The upgraded hash of a user could not be stored; retried at the next login. |
| 3002 | `ApiKeyLastUseNotRecorded` | Warning | `ElPaqueto.Security.ApiKeys.ApiKeyService` | The last use of a key could not be written; the key was accepted. |
| 3101 | `InvalidSecuritySetting` | Error | `ElPaqueto.Security.Configuration` | A `Security` setting is invalid (names the setting, never its value). |
| 3102 | `UnconvertibleSecuritySetting` | Error | `ElPaqueto.Security.Configuration` | A `Security` value cannot be converted to its type. |
| 3103 | `SecurityModuleNotRegistered` | Error | `ElPaqueto.Security.Configuration` | The host did not register the security module. |
| 3104 | `InvalidSecurityConfiguration` | Error | `ElPaqueto.Security.Configuration` | Summary line after the errors above. |
| 3201 | `SecurityDatabaseUpToDate` | Debug | `ElPaqueto.Security.Data.AuthDatabaseMigrator` | No pending migration. |
| 3202 | `SecurityDatabaseMigrating` | Information | `ElPaqueto.Security.Data.AuthDatabaseMigrator` | Pending migrations are being applied. |
| 3203 | `SecurityDatabasePermissionsInherited` | Information | `ElPaqueto.Security.Data.AuthDatabaseMigrator` | Windows: the database file inherits its folder's permissions. A hint logged at every start, so not an audit event. |

## User names in logs

A user name sent by a client is logged in clear only when it names an **existing account** (reasons `disabled` and `bad_password`), and then as stored in the security database. A name that matches no account is always logged as `<unknown:xxxxxxxx>` (`AuditValues.UnknownUserName`): the first 4 bytes, in hexadecimal, of an HMAC-SHA256 of the UTF-8 value under a 256-bit key drawn at process start. A password or an API key typed in the user name field can be made only of characters the user name rules allow (an API key token is), so following the rules is not enough to be logged. As a second barrier, `AuditValues.UserName` logs `<invalid:xxxxxxxx>` instead of any name that breaks the user name rules or contains the API key prefix `epq_`. So:

- a password or an API key typed in the user name field is never logged, only a fingerprint that lets an operator see the same value being retried;
- a user name with CR, LF or other control characters cannot forge log lines (log injection);
- the fingerprint is not stable across restarts and cannot be precomputed for a guessed value;
- the price: an operator does not see which nonexistent names are tried (for example `admin`), only how often the same one is.

## What is never logged

Never, in any category, at any level, as a message, a structured property, a scope or an exception message:

- passwords, in clear or base64, and password hashes;
- the values of the `Authorization` and `X-NuGet-ApiKey` headers, and API key tokens and secrets (key ids only);
- connection strings;
- request bodies. No request logging middleware is registered.

How this holds:

- The failure messages given to `AuthenticateResult.Fail` are fixed strings (the ASP.NET Core authentication handler logs them).
- Exception messages raised by ElPaqueto code are written not to contain a credential; configuration binding errors, whose message repeats the value, are not logged (event 3102 instead). The admin commands never put a password in an exception. Exceptions raised by libraries (database providers, for example) are logged as they are, with their stack, by event 2099 and by ASP.NET Core; their text is outside ElPaqueto's control.
- Entity Framework Core's sensitive data logging stays off, so SQL parameter values are never logged.
- `LoggingGuardTests` fails if any source under `src/` calls `UseHttpLogging`, `AddHttpLogging`, `UseW3CLogging`, `AddW3CLogging` or `EnableSensitiveDataLogging`.

If HTTP logging is ever added: `HttpLoggingOptions.RequestHeaders` is an **allow-list**; headers that are not listed are written as `[Redacted]`. In .NET 10 the default list is `Accept`, `Accept-Charset`, `Accept-Encoding`, `Accept-Language`, `Allow`, `Cache-Control`, `Connection`, `Content-Encoding`, `Content-Length`, `Content-Type`, `Date`, `DNT`, `Expect`, `Host`, `Max-Forwards`, `Range`, `Sec-WebSocket-Extensions`, `Sec-WebSocket-Version`, `TE`, `Trailer`, `Transfer-Encoding`, `Upgrade`, `User-Agent`, `Warning`, `X-Requested-With`, `X-UA-Compatible` (a test pins that `Authorization`, `Proxy-Authorization`, `Cookie` and `X-NuGet-ApiKey` are not in it). Never add `Authorization` or `X-NuGet-ApiKey`, and never log request bodies (`RequestBody`).

**Errors.** In `Production`, an unhandled exception gives an empty 500 response; the exception is logged by ASP.NET Core with its message and stack, which hold no credential. In `Development`, the developer exception page shows the request headers, including `Authorization`, to whoever triggers the error: never expose a `Development` instance (event 1904 warns at startup).

## Tests

- `tests/ElPaqueto.Security.Tests/Logging/RecordingLoggerProvider.cs`: an `ILoggerProvider` (with external scopes) that records the category, level, event id and name, formatted message, every structured property, every active scope and the full exception text of each entry. `LogRecords.AssertContainNone` searches all of it. Shared with `ElPaqueto.Tests` by a link in the project file.
- `tests/ElPaqueto.Tests/AuditLoggingIntegrationTests.cs`: through the whole server pipeline at `Trace` level for every category, with a sentinel password unique to the run, drives a valid login, a wrong password, an unknown user, a disabled user, two malformed headers, a password typed as user name (with and without characters outside the rules), API keys typed as user name, a user name with CR/LF, valid API keys (header and Basic), a key with a wrong secret, a malformed key, a throttled login (trip and 429), two authorization denials (one with a request body holding the sentinel), a remote plain-HTTP request, and requests through the trusted loopback proxy. It checks each event, its level and its properties (the forwarded client address included), and that no entry of any category contains the password, its base64, any `Authorization` value sent or its base64 part, the API key tokens and secrets, or the connection strings.
- `tests/ElPaqueto.Security.Tests/Auditing/AdminCommandAuditTests.cs`: every `admin` command, successful and refused, with sentinel passwords: one event per change, and no password, base64 of a password or stored hash in any entry.
- `tests/ElPaqueto.Security.Tests/Auditing/AuditLoggingProcessTests.cs`: the built host with the shipped `appsettings.json`: audit events at `Information` are shown (on standard error for an `admin` command, whose standard output only carries its result); successes are not shown; no password is printed.
- `AuditValuesTests`, `AuditLogCatalogueTests`, `LoggingGuardTests`: the user name policy, the catalogue rules and the guards above.

The redaction tests were checked by mutation: logging the raw user name, logging an unknown user name in clear when it follows the rules, logging every plain-HTTP refusal at Warning, the password, the token as key id, the `Authorization` header in a scope, the password in an exception message, a password hash in an administration event, a duplicate or undocumented id, or the proxy's address instead of the client's, each makes a test fail.
