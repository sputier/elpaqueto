# Administration from the command line

ElPaqueto has no web administration interface. Users are managed with the `admin` commands of the server binary, run against the same configuration and data as the server.

ElPaqueto ships **without any account and without any default password**. The first administrator is created with [`admin bootstrap`](#first-administrator).

## Security model

The `admin` commands do **not** authenticate their caller. Whoever can run the server binary with access to its configuration and its security database (`Security:Database:ConnectionString`) is an administrator. Protect access to the host, the container and the data volume accordingly: shell access to the server is administrative access.

The commands:

- create and migrate the security database when needed, exactly as the server does at startup;
- never create or migrate the package database, never start the web server and never bind a port, so they can run next to a running server;
- never print or log passwords or password hashes.

Changes are written to the security database at once. They apply to a running server on the same data without a restart.

## Running the commands

Locally, from the folder that contains the server and its `appsettings.json`:

```bash
dotnet ElPaqueto.dll admin <command> [arguments]
```

With Docker Compose, as a one-off container with the same image, volumes, configuration and secrets as the service (the image's entry point is `dotnet ElPaqueto.dll`, so the arguments follow the service name):

```bash
docker compose run --rm elpaqueto admin <command> [arguments]
```

Inside the running container:

```bash
docker compose exec elpaqueto dotnet ElPaqueto.dll admin <command> [arguments]
```

`elpaqueto` is the name of the service in your `compose.yaml`. Every command accepts `--help`.

Settings are read from the same sources as the server ([configuration](configuration.md)), except command-line arguments, which the `admin` commands reject: use `appsettings.json`, environment variables or `/run/secrets`. An invalid security setting stops the command with exit code 1 before it does anything.

## Passwords

A password is never accepted as a command-line argument: the arguments of a process are visible to other users of the machine (`ps`, `/proc`) and shells keep them in their history. There is no `--password` option; passing one is a usage error (exit code 2), and its value is not repeated in the error message.

Commands that set a password read it from, in this order:

| Source | Use |
| --- | --- |
| `--password-file <path>` | Reads the file. One line; a single trailing line break (`\n` or `\r\n`) is ignored. The file must be UTF-8 (a byte order mark is ignored), at most 8 KiB. Intended for Docker secrets in `/run/secrets/<name>`. Error messages never repeat the path, in case a password was typed in its place. |
| `--password-stdin` | Reads the first line of standard input, for example from a pipe or a secret manager. The line is decoded with the input encoding of the process (UTF-8 on Linux and in containers); a trailing `\r` is ignored. |
| Prompt | When neither option is given and standard input is a terminal, the password is asked twice, without echo. |

If no option is given and standard input is not a terminal (a script, a CI job, `docker compose run -T`), the command fails with exit code 2 instead of waiting for input. Giving both options is a usage error.

The password must satisfy the [password policy](configuration.md#securitypasswords): at least `Security:Passwords:MinimumLength` characters (15 by default), at most 256, and different from the user name. A refused password changes nothing.

## User names

A user name has 1 to 64 characters taken from `A-Z`, `a-z`, `0-9`, `.`, `_` and `-`. User names are unique ignoring case: `Alice` and `alice` are the same user. The name keeps the case it was created with.

## First administrator

```bash
dotnet ElPaqueto.dll admin bootstrap --username <name> [--password-file <path> | --password-stdin]
```

`admin bootstrap` creates an active user with the `read`, `publish` and `admin` permissions (the `administrator` preset). It succeeds only when **no user holding the `admin` permission exists**, active or disabled. Users without `admin` do not prevent it.

- It cannot be forced. Once an administrator exists, the command exits with code 3 and changes nothing, which makes it safe to run from automation: treat exit code 3 as "already bootstrapped".
- The check and the creation happen in one database write transaction. If two bootstraps run at the same time, exactly one succeeds; the other exits with code 3.
- If a user with the requested name already exists without `admin`, the command exits with code 3.

Examples:

```bash
# In a terminal: the password is prompted for twice, without echo.
dotnet ElPaqueto.dll admin bootstrap --username admin

# From a file (the trailing newline is ignored).
dotnet ElPaqueto.dll admin bootstrap --username admin --password-file ./admin_password

# From standard input.
my-secret-tool read elpaqueto-admin | dotnet ElPaqueto.dll admin bootstrap --username admin --password-stdin
```

### With Docker Compose and a secret

```yaml
services:
  elpaqueto:
    image: <your ElPaqueto image>
    volumes:
      - elpaqueto-data:/data
    secrets:
      - elpaqueto_admin_password

secrets:
  elpaqueto_admin_password:
    file: ./elpaqueto_admin_password.txt

volumes:
  elpaqueto-data:
```

```bash
# Write the password without leaving it in the shell history, then restrict the file.
read -rs -p "Password: " password && printf '%s\n' "$password" > elpaqueto_admin_password.txt && unset password
chmod 600 elpaqueto_admin_password.txt

docker compose run --rm elpaqueto admin bootstrap --username admin --password-file /run/secrets/elpaqueto_admin_password
```

After the bootstrap, remove the secret from the service and delete the file: the password is only needed once. The server also loads every file in `/run/secrets` as a configuration value, named after the file; a name such as `elpaqueto_admin_password` matches no setting and is ignored.

### Recovery

`admin bootstrap` never replaces an existing administrator, even a disabled one. This version has no command yet to re-enable a user or reset a password: keep a backup of the security database (see [Security database](../technical/security-database.md)) and restore it if the only administrator becomes unusable.

## Exit codes

Every `admin` command uses the same exit codes:

| Code | Meaning |
| --- | --- |
| `0` | Success. |
| `1` | Unexpected error, for example the database cannot be opened or stayed locked. Retrying may help. |
| `2` | Usage or validation error: unknown option or argument, missing value, invalid user name, missing password source, password refused by the policy. Nothing was changed. |
| `3` | Precondition failed, for example an administrator already exists. Nothing was changed. |
| `4` | The user does not exist. |

Results are written to standard output, errors and prompts to standard error. Log messages go to the logging providers configured for the server: by default the console logger, which writes to standard output, at level `Warning` and above. The structured events of the administration commands (category `ElPaqueto.Security.Administration`) are at level `Information`, so they are not shown by default; set `Logging__Console__LogLevel__ElPaqueto.Security.Administration=Information`, or configure another provider, to keep them.
