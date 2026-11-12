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

More settings are added to this page as the features that need them are released.
