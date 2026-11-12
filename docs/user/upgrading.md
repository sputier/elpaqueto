# Upgrading

No release of ElPaqueto is published yet, so there is no image tag or release policy to follow: you upgrade by building a newer commit of this repository. This page describes what happens when you do. To move from BaGetter, read [Upgrading from BaGetter](upgrading-from-bagetter.md) instead.

## Procedure

1. Read the changes between your version and the new one (pull requests and commit messages), in particular anything about settings and migrations.
2. [Back up](backup.md) the volume or the data, with the server stopped.
3. Build the new image (`docker build -t elpaqueto:<new tag> .`).
4. Replace the container with one from the new image, with the same volume and settings.
5. Check the startup log, the health check (`docker ps` shows `healthy`) and a restore from a client.

## Migrations

Both databases are migrated automatically at startup, before the server accepts requests:

- the **security database** (`auth.db`) always, whatever `RunMigrationsAtStartup` says. If it cannot be migrated, the server stops with a non-zero exit code;
- the **package database**, when `RunMigrationsAtStartup` is `true` (the default).

The `admin` commands migrate the security database too, so running a newer image's `admin` command against the volume upgrades it.

An invalid or removed setting stops the server at startup with exit code 1 and a message naming the setting (never its value): fix the configuration and start again.

## Rollback

**Downgrading is not supported once the newer version has applied a migration.** An older version does not know the newer schema. To roll back, stop the server, restore the backup made before the upgrade, and start the older image on it. Changes made after the upgrade (new packages, users, keys, revocations) are lost; review the users and keys as described in [Restoring an older backup](backup.md#restoring-an-older-backup).
