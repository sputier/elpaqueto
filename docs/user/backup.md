# Backup and restore

## What to back up

| Data | Default location in the container | Setting |
| --- | --- | --- |
| Package and symbol files | `/data/packages`, `/data/symbols` | `Storage:Path` (file system storage) |
| Package metadata (SQLite) | `/data/db/elpaqueto.db` | `Database:ConnectionString` |
| Users, permissions and API keys (SQLite) | `/data/db/auth.db` | `Security:Database:ConnectionString` |

Back up all three **together**: packages without their metadata are not listed, and metadata without the files cannot be downloaded. The security database holds password hashes and API key hashes: protect the backup like the server itself.

Both databases use SQLite's write-ahead log. Copying `*.db` files while the server runs can produce an inconsistent copy. Stop the server first: a server that stopped normally leaves no `-wal` or `-shm` file behind. Configuration and secrets (environment variables, `appsettings.json`, files in `/run/secrets`) are not on the volume: keep them in your deployment repository or secret store.

With another database (PostgreSQL, SQL Server) or another storage provider, use that system's own backup for the package data, and still back up `auth.db`.

## Docker volume: backup

With the `elpaqueto-data` volume of the [installation](installation.md) page, from the folder that receives the archive:

```bash
docker stop elpaqueto
docker run --rm -v elpaqueto-data:/data:ro -v "$PWD":/backup alpine:3 \
  sh -c 'umask 077 && tar czf /backup/elpaqueto-data.tar.gz -C /data .'
docker start elpaqueto
```

The archive keeps the owner (UID 1654) and the modes of every file, including the owner-only `auth.db` and `/data/db`. `umask 077` makes the archive readable by its owner only; on a Linux host that owner is root (the helper container runs as root), so move it with `sudo` or `sudo chown` it to the account that handles backups. With Docker Compose, stop the service with `docker compose stop elpaqueto` and use the volume name Compose created (`docker volume ls`).

## Docker volume: restore

Into a new, empty volume, so that the current one stays untouched until the restore is checked:

```bash
docker volume create elpaqueto-data-restored
docker run --rm -v elpaqueto-data-restored:/data -v "$PWD":/backup:ro alpine:3 \
  tar xzpf /backup/elpaqueto-data.tar.gz -C /data
```

Then start the server on `elpaqueto-data-restored` with the same settings, check that users can sign in and that packages restore, and remove the old volume only after that.

This procedure was run against the image built from this repository: a backup of a stopped container, a restore into a new volume, and a server started on it with the same users, API keys and packages. It is not covered by an automated test yet.

## Online backup

To back up without stopping the server, copy each SQLite database with SQLite's online backup (for example `sqlite3 auth.db ".backup '/backup/auth.db'"` or `VACUUM INTO`), then copy the package files. The image does not ship the `sqlite3` tool, and this variant has not been tested by this project: prefer the stopped-container procedure above.

## Restoring an older backup

Restoring a backup brings back the users, permissions and API keys **as they were**: a key revoked or a user disabled after the backup works again. After a restore, review `admin user list --include-disabled` and `admin key list --include-revoked`, and revoke again what must stay revoked.

A backup made by an older version can be restored and then upgraded (migrations run at startup). A backup made after an upgrade cannot be used with the older version: see [upgrading](upgrading.md).
