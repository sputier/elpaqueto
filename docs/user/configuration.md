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

More settings are added to this page as the features that need them are released.
