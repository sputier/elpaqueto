# Secrets handling

How ElPaqueto stores, compares and keeps out of logs the secrets it handles. Each section summarises the design and links to the page that describes it fully.

## Passwords

[Password hashing](passwords.md):

- `PasswordHasher<T>` from ASP.NET Core Identity, format V3: PBKDF2 with HMAC-SHA512, a random 128-bit salt, a 256-bit derived key, `Security:Passwords:IterationCount` iterations (210000 by default).
- Passwords are Unicode-normalized before hashing and limited to 256 characters; the policy requires `Security:Passwords:MinimumLength` characters (15 by default), refuses a password equal to the user name and a password with the API key format.
- A hash made with a lower cost is re-hashed after the next successful sign-in.
- Unknown and disabled users go through the same verification work as a real user, so the response time does not tell whether a user exists.
- A successful verification is remembered in memory for `Security:Authentication:VerificationCacheTtl` as an HMAC of the stored hash and the password under a per-process random key; failures are never cached ([authentication](authentication.md#verification-cache)).
- The `admin` commands never accept a password as an argument: prompt without echo, `--password-stdin` or `--password-file` ([administration commands](admin-cli.md#password-input)).

## API keys

[API keys](api-keys.md):

- Format `epq_<keyId>_<secret>`: a 12-character public key id and a 43-character base62 secret (about 256 bits) drawn from `RandomNumberGenerator` by rejection sampling. The `epq_` prefix makes a leaked key recognizable by secret scanners.
- Only the SHA-256 of the secret is stored. A slow hash is not needed for a 256-bit random secret and would make every request expensive.
- Validation looks up the key id, then compares the hashes in fixed time (`CryptographicOperations.FixedTimeEquals`); an unknown key id does the same comparison work.
- The key is printed once, alone on standard output, by `admin key create` and `admin key rotate`; it is never logged and cannot be displayed again.

## In transit

- Basic credentials and API keys are only accepted over HTTPS from remote clients: a remote plain-HTTP request is refused before its credentials are read, without a challenge and without a redirect ([forwarded headers](forwarded-headers.md)).
- Cross-origin requests never run in credentials mode ([web security](web-security.md#cors)).
- Authenticated responses carry `Cache-Control: no-store` for pages and errors; reverse proxies must not cache responses to requests that carry credentials ([reverse proxy](../user/reverse-proxy.md#response-headers)).

## In logs

[Audit logging](audit-logging.md#what-is-never-logged): passwords, password hashes, `Authorization` and `X-NuGet-ApiKey` values, API key secrets, connection strings and request bodies are never logged. User names that match no account are logged as a fingerprint. `LoggingGuardTests` fails if a source enables request logging or EF Core sensitive data logging. Configuration errors name the setting, never the value.

## In configuration

- Configuration secrets (connection strings with credentials, mirror credentials) come from environment variables or from files in `/run/secrets` (key-per-file), which take precedence over environment variables ([configuration](../user/configuration.md#sources-and-precedence)). `appsettings.json` must not hold secrets.
- Invalid values are reported by setting name only; a configuration binding failure prints exception types and stack traces without messages, because the binder's message quotes the value (`Program.IsConfigurationBindingFailure`).
- An unhandled error ends the process with exit code 1 instead of a crash, so that no core dump with process memory is written.

## At rest

- `auth.db` is created with mode `0600` in a `0700` folder on Unix, and a world-readable existing file is narrowed at startup ([security database](security-database.md#location)). On Windows it inherits its folder's permissions.
- Backups contain the hashes: they are protected like the server ([backup](../user/backup.md)).
- ASP.NET Core data protection keys (antiforgery tokens of the web UI) are kept in memory in the container; they protect no credential.
