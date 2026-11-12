# Administration from the command line

ElPaqueto has no web administration interface. Users are created and managed with the `admin` commands of the server binary, run against the same configuration and data as the server.

ElPaqueto ships **without any account and without any default password**. The first administrator is created with [`admin bootstrap`](#first-administrator).

## Security model

The `admin` commands do **not** authenticate their caller. Whoever can run the server binary with access to its configuration and its security database (`Security:Database:ConnectionString`) is an administrator. Protect access to the host, the container and the data volume accordingly: shell access to the server is administrative access.

The commands:

- create and migrate the security database when needed, exactly as the server does at startup;
- never create or migrate the package database, never start the web server and never bind a port, so they can run next to a running server;
- never print or log passwords or password hashes.

Changes are committed to the security database at once; no restart is needed. The server reads the user on every request, so a disabled user, a reset password or new permissions take effect on the next request.

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

A user name has 1 to 64 characters taken from `A-Z`, `a-z`, `0-9`, `.`, `_` and `-`. User names are unique ignoring case: `Alice` and `alice` are the same user. The name keeps the case it was created with. This is the same rule Basic authentication applies, so a user created by these commands can always sign in with its name.

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
# Without Swarm, Compose mounts the file as it is (the uid, gid and mode options of a secret are ignored):
# it must be readable by the container user, UID 1654, and by nobody else.
sudo chown 1654:1654 elpaqueto_admin_password.txt && sudo chmod 0400 elpaqueto_admin_password.txt

docker compose run --rm elpaqueto admin bootstrap --username admin --password-file /run/secrets/elpaqueto_admin_password
```

A secret file that the container user cannot read (for example `chmod 600` with your own user as owner, on a Linux host) stops the server and every command at startup with exit code 1 and `ElPaqueto could not load its configuration: Access to the path '/run/secrets/...' is denied.`, because every file in `/run/secrets` is read as configuration. [`docker-compose.example.yml`](../../docker-compose.example.yml) contains the same setup. After the bootstrap, remove the secret from the service and delete the file: the password is only needed once. The server also loads every file in `/run/secrets` as a configuration value, named after the file; a name such as `elpaqueto_admin_password` matches no setting and is ignored.

### Recovery

`admin bootstrap` never replaces an existing administrator, even a disabled one. To recover access, use the user commands below from the server's shell:

- the only administrator was disabled: `admin user enable <name>`;
- its password is lost: `admin user reset-password <name>`;
- another administrator is needed: `admin user create <name> --permissions administrator`.

## Managing users

All commands below follow the conventions above (password sources, exit codes, output streams). User names are matched ignoring case.

| Command | Effect |
| --- | --- |
| `admin user create <username> --permissions <list> [password source]` | Creates an active user. Exit code 3 if the name exists (ignoring case). |
| `admin user list [--format text\|json] [--include-disabled]` | Lists the active users, or every user with `--include-disabled`, ordered by name. |
| `admin user show <username> [--format text\|json]` | Shows one user. |
| `admin user disable <username>` | Disables the user. Already disabled: exit code 0 with a notice. |
| `admin user enable <username>` | Enables the user. Already enabled: exit code 0 with a notice. |
| `admin user set-permissions <username> --permissions <list>` | Replaces the permission set. The same set: exit code 0 with a notice. |
| `admin user reset-password <username> [password source]` | Sets a new password and its change date. A disabled user stays disabled. |

`show`, `disable`, `enable`, `set-permissions` and `reset-password` exit with code 4 when the user does not exist (`reset-password` checks this before asking for the password). Every change updates the user's `updatedAt` date; a command that changes nothing leaves it as it was.

`reset-password` changes only the password. It does not affect other credentials of the user; if the account may be compromised, also review them.

### Permissions

`--permissions` takes a comma-separated, case-insensitive list. Each entry is a permission or a preset, and the result is their union:

| Entry | Grants |
| --- | --- |
| `read` | Browse and download packages. |
| `publish` | Publish packages. |
| `admin` | Administer the server. |
| `reader` | `read` |
| `publisher` | `read`, `publish` |
| `administrator` | `read`, `publish`, `admin` |

The permissions are independent: `admin` does not imply `read` or `publish`. An unknown entry is a usage error (exit code 2) that lists the valid values. An empty list is refused: use `disable` to remove all access.

### Last active administrator

`disable` and `set-permissions` refuse (exit code 3, nothing changed) a change that would leave no **active** user holding `admin`. With two active administrators, one of them can be disabled or demoted. The check and the change happen in one database write transaction, so two concurrent commands cannot each remove "the other" last administrator.

### No deletion

Users cannot be deleted. Deleting a user would leave dangling references to it; disabling removes its access and keeps its history. Disable users who must no longer have access.

### Examples

```bash
# A user for a CI pipeline, password from a file.
dotnet ElPaqueto.dll admin user create ci --permissions publisher --password-file ./ci_password

# A read-only user, password prompted for.
dotnet ElPaqueto.dll admin user create alice --permissions reader

# Give alice publish rights too, then list everybody as JSON.
dotnet ElPaqueto.dll admin user set-permissions alice --permissions read,publish
dotnet ElPaqueto.dll admin user list --include-disabled --format json

# Remove access.
dotnet ElPaqueto.dll admin user disable alice

# In Docker Compose.
docker compose run --rm elpaqueto admin user reset-password ci --password-file /run/secrets/ci_password
```

### JSON output

`list --format json` writes one object with a `users` array; `show --format json` writes one user object. The shape is stable:

```json
{
  "users": [
    {
      "id": "6f1c2a0e-8d1b-4c63-9a55-0d4f3c2b1a90",
      "userName": "ci",
      "isActive": true,
      "permissions": ["read", "publish"],
      "createdAt": "2026-11-12T08:30:00.0000000Z",
      "updatedAt": "2026-11-12T08:30:00.0000000Z",
      "passwordChangedAt": "2026-11-12T08:30:00.0000000Z"
    }
  ]
}
```

- `id`: the user identifier (GUID).
- `permissions`: always in the order `read`, `publish`, `admin`; only the granted ones.
- Dates are UTC, ISO 8601, with seven fractional digits and a `Z` suffix.
- The output never contains a password hash.

Log lines from the console logger also go to standard output: keep the default log level (`Warning`) or send logs elsewhere when a script parses the JSON.

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
