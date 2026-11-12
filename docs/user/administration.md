# Administration from the command line

ElPaqueto has no web administration interface. Users and API keys are created and managed with the `admin` commands of the server binary, run against the same configuration and data as the server.

ElPaqueto ships **without any account and without any default password**. The first administrator is created with [`admin bootstrap`](#first-administrator).

## Security model

The `admin` commands do **not** authenticate their caller. Whoever can run the server binary with access to its configuration and its security database (`Security:Database:ConnectionString`) is an administrator. Protect access to the host, the container and the data volume accordingly: shell access to the server is administrative access.

The commands:

- create and migrate the security database when needed, exactly as the server does at startup;
- never create or migrate the package database, never start the web server and never bind a port, so they can run next to a running server;
- never print or log passwords or password hashes;
- print an API key once, when `admin key create` or `admin key rotate` creates it, and never log it.

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

Standard output only carries the result: the `admin` commands send console log lines to standard error, so a script can parse the JSON whatever the log level.

## API keys

An API key lets a client (a CI job, `dotnet nuget push`, a restore) authenticate as its owner with some of the owner's permissions, without the owner's password. How clients send keys is described in [Using an API key](configuration.md#using-an-api-key). Keys are managed by the `admin key` commands:

| Command | Effect |
| --- | --- |
| `admin key create --user <username> --name <label> --permissions <list> (--expires <when> \| --no-expiry) [--format text\|json]` | Creates a key for the user and prints it once. |
| `admin key list [--user <username>] [--include-revoked] [--format text\|json]` | Lists the keys that are not revoked, or all of them with `--include-revoked`, oldest first. |
| `admin key show <keyId> [--format text\|json]` | Shows one key. |
| `admin key revoke <keyId>` | Revokes the key at once. Already revoked: exit code 0 with a notice. |
| `admin key rotate <keyId> (--expires <when> \| --no-expiry) [--format text\|json]` | Creates a new key with the same owner, name and permissions, revokes the old one, and prints the new key once. |

The **key id** is the public part of a key: the 12 characters after `epq_` (`epq_<keyId>_<secret>`). `list` shows it. Give the commands the key id, never the complete key: a complete key given where a key id is expected is refused (exit code 2) without being repeated, but it is then in your shell history, so rotate or revoke it.

### Creating a key

- `--permissions` is required: a key gets no permission by default. It takes the same values as for users (`read`, `publish`, `admin` and the presets, see [Permissions](#permissions)), and **every permission must be held by the owner** now, `admin` included; otherwise the command exits with code 3 and creates nothing. Give each key only what its client needs: `read` for restore, `publish` for pushes.
- The expiry must be stated: `--expires <when>` or `--no-expiry`. `<when>` is a number of days (`90d`, from 1 to 36500), a date (`2027-01-31`, meaning 00:00 UTC that day) or a time with `Z` or an offset (`2027-01-31T12:00:00Z`, `2027-01-31T14:00:00+02:00`). A time in the past is refused (exit code 2). Prefer keys that expire, and rotate them before they do.
- `--name` is a label of 1 to 100 characters without control characters, for example the job that uses the key (`ci-main`). It need not be unique, and cannot start with `epq_`.
- A value that looks like an API key (it starts with `epq_`) given to `--user`, `--name`, `--permissions` or as a key id is never repeated in an error message.
- The owner must exist (exit code 4 otherwise) and be active (exit code 3 otherwise). User names are matched ignoring case.

**The key is printed once, and nowhere else.** `create` and `rotate` write the key alone, followed by a line feed, to **standard output**; the summary and the warning go to standard error. Only a hash of the key is stored: it cannot be displayed again, and a lost key is revoked and replaced. Redirect standard output to keep the key out of the terminal and of logs:

```bash
# Into a file that only you can read.
(umask 077 && dotnet ElPaqueto.dll admin key create --user ci --name ci-main --permissions publish --expires 90d > ci-main.key)

# Straight into a secret store (any tool that reads the secret from standard input).
dotnet ElPaqueto.dll admin key create --user ci --name ci-main --permissions publish --expires 90d | my-secret-tool write elpaqueto-publish-key
```

With `--format json`, standard output is one JSON object with the key in `token` and its description in `key` (and, for `rotate`, the replaced key id in `replacedKeyId`).

### Listing and inspecting

`list` and `show` print the key id, name, owner, status, permissions, effective permissions, and the creation, expiry, revocation and last-use dates (UTC). They never print the key or its hash.

- **Status**: `active`, `revoked`, `expired` or `owner-disabled`, in this order of precedence. Only an `active` key authenticates.
- **Effective permissions**: the key's permissions that its owner still holds. A key never grants more than its owner holds *now*: removing a permission from a user restricts all of the user's keys at the next request, and disabling a user stops all of them.
- **Last used** is recorded at most once per `Security:ApiKeys:LastUsedUpdateInterval` (5 minutes by default), so it is accurate to within that interval.

### Revoking and rotating

`revoke` takes effect on the next request: there is no cache. Revoking a key twice is not an error. An unknown key id exits with code 4.

`rotate` replaces a key in one database transaction: the new key has the same owner, name and permissions, and the old key stops working at once. The expiry of the new key must be stated again. It is refused (exit code 3, nothing changed) for a revoked key, for a disabled owner, and when the owner no longer holds every permission of the key: rotation never silently changes what a key may do, so revoke the key and create one with the permissions wanted.

Because the old key stops working immediately, `rotate` interrupts the clients that use it until they get the new key. To switch clients without interruption, rotate by hand: `create` a new key, update the clients, then `revoke` the old key.

### Examples

```bash
# A Publish key for the CI push job, valid 90 days, written to a file.
(umask 077 && dotnet ElPaqueto.dll admin key create --user ci --name ci-push --permissions publish --expires 90d > ci-push.key)

# A Read key for restore jobs, without expiry, as JSON.
dotnet ElPaqueto.dll admin key create --user ci --name ci-restore --permissions read --no-expiry --format json

# Keys of one user, revoked ones included.
dotnet ElPaqueto.dll admin key list --user ci --include-revoked

# Replace a key, then revoke another one.
dotnet ElPaqueto.dll admin key rotate AbCdEfGhIjKl --expires 2027-06-30 > ci-push.key
dotnet ElPaqueto.dll admin key revoke MnOpQrStUvWx

# In Docker Compose: -T keeps the key on standard output instead of a pseudo-terminal.
docker compose run --rm -T elpaqueto admin key create --user ci --name ci-push --permissions publish --expires 90d > ci-push.key
docker compose exec -T elpaqueto dotnet ElPaqueto.dll admin key revoke AbCdEfGhIjKl
```

The key ids above are placeholders. Then use the key as described in [Using an API key](configuration.md#using-an-api-key), for example `dotnet nuget push <package> --source <source> --api-key "$(cat ci-push.key)"`.

### JSON shape of a key

`list --format json` writes `{"keys": [ ... ]}`; `show --format json` writes one key; `create` and `rotate` write `{"token": "epq_...", "replacedKeyId": "...", "key": { ... }}` (`replacedKeyId` for `rotate` only):

```json
{
  "keyId": "AbCdEfGhIjKl",
  "name": "ci-push",
  "ownerId": "6f1c2a0e-8d1b-4c63-9a55-0d4f3c2b1a90",
  "owner": "ci",
  "status": "active",
  "permissions": ["publish"],
  "effectivePermissions": ["publish"],
  "createdAt": "2026-11-12T08:30:00.0000000Z",
  "expiresAt": "2027-02-10T08:30:00.0000000Z",
  "revokedAt": null,
  "lastUsedAt": null
}
```

Dates are UTC, ISO 8601, with seven fractional digits and a `Z` suffix; `expiresAt`, `revokedAt` and `lastUsedAt` are `null` when not set. Permission lists are always in the order `read`, `publish`, `admin`.

## Exit codes

Every `admin` command uses the same exit codes:

| Code | Meaning |
| --- | --- |
| `0` | Success. |
| `1` | Unexpected error, for example the database cannot be opened or stayed locked. Retrying may help. |
| `2` | Usage or validation error: unknown option or argument, missing value, invalid user name, missing password source, password refused by the policy, missing or past key expiry, malformed key id. Nothing was changed. |
| `3` | Precondition failed, for example an administrator already exists, a key permission the owner does not hold, a disabled owner, or a revoked key to rotate. Nothing was changed. |
| `4` | The user or the API key does not exist. |

Results are written to standard output, errors and prompts to standard error. Log messages go to the logging providers configured for the server: by default the console logger at level `Warning` and above, which the `admin` commands send to **standard error**, so standard output only carries results (and the new key of `admin key create` and `rotate`). The structured events of the administration commands (category `ElPaqueto.Security.Administration`) are at level `Information`, so they are not shown by default; set `Logging__Console__LogLevel__ElPaqueto.Security.Administration=Information`, or configure another provider, to keep them.
