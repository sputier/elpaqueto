# API keys

Per-user API keys are stored in the [security database](security-database.md) and managed by `IApiKeyService` (`src/ElPaqueto.Security/ApiKeys/`). This page describes the token format, the storage, the validation rules and how requests use keys. The `admin key` commands ([administration commands](admin-cli.md#api-key-commands), [user guide](../user/administration.md#api-keys)) create, list, revoke and rotate keys: like the authentication handler, they call this service and do not re-implement any of it.

## How requests use keys

A key is accepted in two places, both handled by the authentication handler ([authentication](authentication.md)):

| Sent as | Used by | Accepted on | Credential source claim |
| --- | --- | --- | --- |
| `X-NuGet-ApiKey: <token>` | `dotnet nuget push --api-key`, `dotnet nuget delete --api-key` | every route the key's effective permissions open, **including writes** | `x-nuget-apikey` |
| Basic password, any non-empty user name (`apikey` by convention) | restore and every other request that uses the source credentials of `NuGet.Config` or `NuGetPackageSourceCredentials_<source>` | every route the key's effective permissions open, **except writes** | `basic-password` |

- When `X-NuGet-ApiKey` is present it alone decides, even if `Authorization` carries valid credentials, and an invalid value fails authentication: 401 on every protected route. Two values fail.
- Writes (push, symbol push, unlist/delete, relist) need a key with `Publish` **in the header**. A key with `Publish` sent only as a Basic password, or a user password with `Publish`, gets 403 ([authorization](authorization.md)). Stock NuGet clients send the header only on push and delete, and Basic credentials on every other request, so a CI push job gives the source credentials `Read` (a user password or a `Read` key as Basic password, for the service index) and `--api-key` a `Publish` key; one key with `Read` and `Publish` can serve both.
- The user name sent with a key is ignored: the key names its owner. It cannot lend the permissions of the user it names.
- Every key failure (malformed, unknown, wrong secret, revoked, expired, owner disabled) gives the same 401 as a wrong password. The reason is logged with the key id only.
- Because a key used for restore travels on every request, not only on pushes, it must only travel over HTTPS (see `Security:Transport` in the [configuration](../user/configuration.md)) and should be a `Read`-only key separate from the `Publish` key used for pushes.

### The inline check of the inherited write actions

The inherited `PackagePublishController` and `SymbolController` still call `ElPaqueto.Core.IAuthenticationService.AuthenticateAsync(<X-NuGet-ApiKey value>)` before writing (no controller was changed). `AddElPaquetoSecurity` replaces the inherited `ApiKeyAuthenticationService` with `ApiKeyPrincipalAuthenticationService` using `services.Replace`: `TryAdd` would lose, because the inherited registration already exists when the security module is added (ADR 0002, decision 7).

The replacement reads the principal of the current request through `IHttpContextAccessor` and answers `true` only if that principal was authenticated by a key read from `X-NuGet-ApiKey`, the key id of the value passed in is that key's, and the principal holds `Publish`. It never queries the database (a test counts the SQL commands). The endpoint policy has already demanded the same thing, so for an authorized write it always answers `true`: it is defence in depth only.

The legacy `ApiKey` and `Authentication:ApiKeys` settings no longer influence anything: in particular, **leaving them empty no longer opens publishing to everyone**, which was the inherited behaviour. `ApiKeyAuthenticationService.cs` stays in the tree, unused, so that upstream changes still port mechanically. The settings themselves are rejected at startup by `LegacyAuthenticationSettingsValidator`, an `IValidateOptions<ElPaquetoOptions>` registered by the security module: a non-empty `ApiKey`, `Authentication:Credentials:*` or `Authentication:ApiKeys:*` value, from any configuration source, fails the startup validation that runs before the server listens and before any command. The failure names the key path, never the value. The inherited option classes (`NugetAuthenticationOptions`, `NugetCredentials`, `ApiKey`) and the `ElPaquetoOptions` properties stay, for the same porting reason.

## Token format

```text
epq_<keyId>_<secret>
epq_AAAAAAAAAAAA_0000000000000000000000000000000000000000000   (shape only: 12 + 43 base62 characters)
```

| Part | Length | Content |
| --- | --- | --- |
| `epq_` | 4 | fixed prefix: a leaked key is recognizable (log scrubbing, secret scanning) |
| key id | 12 | base62, public: lookup, listing and revocation |
| `_` | 1 | separator |
| secret | 43 | base62 |

- Base62 (`0-9A-Za-z`) contains neither `_` nor `-`, so the token splits unambiguously on `_`, is selected whole by a double-click and is safe in HTTP headers, XML (`NuGet.Config`) and Basic credentials. Base64url would put `_` and `-` inside the secret.
- Every token is exactly 60 characters. The pattern is `ApiKeyToken.Pattern`: `\Aepq_[0-9A-Za-z]{12}_[0-9A-Za-z]{43}\z`. It is anchored with `\z`, not `$`, because `$` in .NET also accepts a trailing line feed. For secret scanning, drop the anchors.
- No normalization is applied: whitespace, another case for the prefix, a separator other than `_`, a non-ASCII digit or letter, or any other length makes the value malformed.
- `ApiKeyToken.TryParse` and `ApiKeyToken.IsTokenFormat` are the only parser. The authentication handler uses them to decide whether a Basic password is a key (ADR 0002, decision 6), and the password policy uses them to refuse passwords that have the token format (see [passwords](passwords.md)). The parsed value's `ToString()` shows the key id and `***`, never the secret.

### Generation

Characters are drawn from `RandomNumberGenerator` by rejection sampling: a random byte from 0 to 247 (4 × 62 values) maps to `byte % 62`, and bytes 248 to 255 are discarded. Each character therefore has exactly four antecedents and the distribution is uniform; taking `byte % 62` of every byte would make the first eight characters more likely. A test enumerates the 256 byte values to prove the mapping, and another checks that rejected bytes are skipped rather than folded.

- Secret: 43 uniform base62 characters, log2(62) × 43 ≈ 256.03 bits. (The issue proposed encoding 32 random bytes in base62; drawing the 43 characters directly gives the same length and at least the same entropy, without a big-integer conversion.)
- Key id: 12 characters, about 71 bits. A collision is improbable; if one happened, the unique index would make the creation fail rather than produce two keys with one id.

## Storage

Table `ApiKeys`, created by the migration `AddApiKeys`:

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | `TEXT` (GUID) | generated by the application |
| `KeyId` | `TEXT`, 12 | **unique index**; check constraint `length = 12` |
| `SecretHash` | `BLOB`, 32 | SHA-256 of the secret; check constraint `length = 32`; never leaves the service |
| `OwnerUserId` | `TEXT` (GUID) | foreign key to `Users`, **`ON DELETE RESTRICT`**, indexed |
| `Name` | `TEXT`, max 100 | label, for example `ci-main`; no control characters |
| `Permissions` | `INTEGER` | `Permission` flags requested for the key; check constraint `0..7` as for `Users` |
| `CreatedAt`, `ExpiresAt`, `RevokedAt`, `LastUsedAt` | `TEXT` | UTC; the last three are nullable |
| `ConcurrencyStamp` | `TEXT` | regenerated by every update made through the change tracker and by revocation |

Why SHA-256 and not a password hash: the secret has 256 bits of uniform entropy, so a stolen hash cannot be brute-forced and precomputed tables are useless, with or without a salt. A slow hash (PBKDF2) would only make every request expensive. There is no pepper (an HMAC key held in configuration) in V1: it would protect a stolen database file, at the cost of key management, and rotating it would invalidate every key.

Why `RESTRICT` and not `CASCADE`: deleting a user must be a deliberate act that deals with their keys first. Disabling a user already invalidates every key of that user immediately, without losing the audit trail.

A test creates, uses and rotates keys on a real SQLite file and scans the bytes of the database file and of its write-ahead log, before and after the checkpoint, for the tokens, the secrets (ASCII and UTF-16) and any 16-character part of a secret. It finds none, while the same scan does find the stored key ids.

## Service

`IApiKeyService` (scoped, registered by `AddElPaquetoSecurity`):

- `CreateAsync(ownerUserId, name, permissions, expiresAt)` returns the key and its token. The token is returned **this once**: it is not stored, not logged, and no other method can return it. The request is refused (`ApiKeyRequestException`, with a message safe to show) when the owner does not exist or is disabled, when `permissions` is `None`, contains undefined bits or contains a flag the owner does not hold now, when the name is empty, longer than 100 characters or contains control characters, or when the expiry is not in the future. `Admin` is granted only when the owner holds it and it is part of the requested value: a key of an administrator gets exactly the flags asked for. No default expiry is applied here; the command line requires the operator to state one (`--expires` or `--no-expiry`).
- `CreateAsync` reads the owner and inserts the key in one transaction: in WAL mode, a change to the owner committed by another connection in between makes the insert fail (busy snapshot) rather than store a key with permissions the owner no longer holds.
- `ListAsync(ownerUserId?)` returns `ApiKeyInfo` values, which have no hash and no secret. `FindAsync(keyId)` returns one, or `null` for an unknown or malformed key id (without a database query for the latter).
- `RevokeAsync(keyId)` is one conditional `UPDATE`: idempotent, it keeps the first revocation time and reports `Revoked`, `AlreadyRevoked` or `NotFound`.
- `RotateAsync(keyId, expiresAt)` creates a key with the same owner, name and permissions and revokes the old one in **one transaction**. If the new key cannot be stored or the old one cannot be revoked, nothing is committed and the old key stays valid; tests inject both failures with SQLite triggers. The revocation is conditional on the key not being revoked yet, so of concurrent rotations of one key exactly one succeeds (tested with four in parallel). A revoked key cannot be rotated. Rotation is refused like a creation if the owner no longer holds every permission of the key: rotation never silently changes what a key may do, so the operator revokes it and creates a key with the permissions wanted. Until then the old key keeps working with its reduced effective permissions.
- `ValidateAsync(token)` is described below.

The service never leaves its own entities tracked by the scoped context, so a caller that reuses the context never works on a stale key.

## Validation order

1. More than 128 characters: refused as `Oversized`, on the length alone.
2. Not the exact token format: refused as `Malformed`. Neither case touches the database (asserted by counting the SQL commands) or echoes the value.
3. SHA-256 of the secret, then one indexed read of the key by key id, joined with its owner.
4. Unknown key id: the computed hash is still compared, with a random dummy hash, so this path costs the same as a wrong secret. `UnknownKey`.
5. Constant-time comparison (`CryptographicOperations.FixedTimeEquals`) with the stored hash: `WrongSecret` on mismatch. The comparison goes through an internal seam (`IApiKeyHashComparer`); a test counts its calls on every path that reaches the database and asserts the registered implementation is the fixed-time one.
6. Only once the secret is proven: `Revoked`, then `Expired` (`ExpiresAt <= now`, from the injected `TimeProvider`), then `OwnerDisabled`.
7. Success: the effective permissions are the key's permissions ∩ the owner's **current** permissions, recomputed on every use. Demoting the owner restricts all their keys at the next request without touching the keys, and restoring the owner restores them. A key whose owner holds none of its permissions still authenticates, with `Permission.None`: authorization then refuses it with 403, as for any authenticated principal without the required flag.

The failure reason (`ApiKeyValidationResult.Failure`) is for logs only. An HTTP response must not reveal it: every failure gives the same 401 (ADR 0002, decision 11). The service does not log refusals itself: the authentication handler logs each one once, with the client address (audit event 1003 `ApiKeyRejected`, see [audit logging](audit-logging.md)). Creation, revocation and rotation are logged by the `admin key` commands (audit events 2010–2012), not by the service. Secrets and tokens never appear in logs or exception messages; a test captures all logging, Entity Framework Core's included, while creating, validating (valid, wrong, unknown and malformed values), rotating and revoking keys, and searches it for every secret.

## Last use: debounced writes

Validation is a read. Recording every use would turn each package download into a write on the security database, and requests would contend for the SQLite writer lock (and with the command-line tool). `LastUsedAt` is therefore written at most once per `Security:ApiKeys:LastUsedUpdateInterval` (default 5 minutes) and per key:

1. The stored `LastUsedAt`, read with the key anyway: no write while it is more recent than the interval. This gate holds across processes and restarts.
2. An in-memory claim per key in a process-wide `ApiKeyUsageTracker`: among concurrent requests that all read the same stale value, only the first one writes. Expired claims are dropped when the table grows (at most once per interval). A stored or claimed time in the future (a clock that moved backwards) does not block writes; the claim still limits them to one per interval.
3. The write itself is conditional (`WHERE LastUsedAt IS NULL OR LastUsedAt <= now - interval`): if another process wrote between this read and this write, the update changes nothing.

The write is a set-based `UPDATE` that leaves `ConcurrencyStamp` unchanged, so a mere use never conflicts with a concurrent revocation or rotation. If it fails (for example the database is busy beyond the busy timeout), a warning naming the key id is logged, the claim is given back so the next use retries, and the key is still accepted. The write waits for the SQLite writer lock like any other write: a request that must record a use can wait behind a long write by another process, at most once per interval and per key. Failed validations never write. Tests cover: 100 uses in the interval give one `UPDATE`; a use after the interval writes again; a recent stored value suppresses the write in a fresh process; 20 concurrent uses give one `UPDATE`; a concurrent writer between the read and the write wins; a failed write does not refuse the key.

`LastUsedAt` is therefore accurate to within the interval.
