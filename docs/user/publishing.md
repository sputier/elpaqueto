# Publishing packages

Every write (push, symbol push, delete or unlist, relist) needs an **API key with `Publish`, sent in the `X-NuGet-ApiKey` header**: the `--api-key` option of the NuGet client. A user password, or a key used as the Basic password of the source, is refused with `403` for writes, even when it holds `Publish`. Create the key with [`admin key create`](administration.md#creating-a-key), for example `--permissions publish --expires 90d`.

`dotnet nuget push` also reads the service index with the **source credentials** before it pushes, so the source needs credentials with `Read` as well: see [client setup](client-setup.md).

The examples use a source named `elpaqueto` and keys in environment variables set from a secret store.

## Push

```bash
dotnet nuget push <package>.nupkg --source elpaqueto --api-key "$ELPAQUETO_PUBLISH_KEY"
```

The server answers `201 Created`. The key is visible in the process arguments while the command runs: on a shared machine, run it from a CI job or a session where other users cannot list your processes.

The maximum package size is `MaxPackageSizeGiB` (8 GiB by default). In the container, uploads are buffered in `/tmp` first: see [Docker](docker.md#tmp-is-required).

## Symbols

When a `.snupkg` file sits next to the `.nupkg` (for example after `dotnet pack -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg`), `dotnet nuget push` pushes it too, with the same key, to the symbol endpoint the service index advertises.

Downloading symbols needs `Read`: a debugger or tool that fetches symbols from ElPaqueto must send Basic credentials. How a given debugger answers the Basic challenge has not been verified.

## Pushing a version that already exists

With the default `AllowPackageOverwrites: false`, pushing an existing version again fails with `409 Conflict`. In CI, add `--skip-duplicate` to treat that as success:

```bash
dotnet nuget push artifacts/*.nupkg --source elpaqueto --api-key "$ELPAQUETO_PUBLISH_KEY" --skip-duplicate
```

`AllowPackageOverwrites` also accepts `PrereleaseOnly` and `true`, which let a push replace the content of an existing version. Any holder of a `Publish` key can then change a version that others have already restored: leave it at `false` unless you need it.

## Unlist and delete

```bash
dotnet nuget delete <PackageId> <version> --source elpaqueto --api-key "$ELPAQUETO_PUBLISH_KEY" --non-interactive
```

What happens depends on `PackageDeletionBehavior`:

| Setting | Effect | Permissions of the key |
| --- | --- | --- |
| `Unlist` (default) | The version is marked unlisted (`"listed": false` in the registration) and hidden from search, but its files stay downloadable: builds that reference that exact version still restore. | `Publish` |
| `HardDelete` | The version and its files are removed for good. Builds that pin it break. | `Publish` and `Admin` |

## Relist

An unlisted version is listed again with a `POST` to the same URL that `dotnet nuget delete` uses. The NuGet clients have no command for it; with `curl`, keeping the key off the command line:

```bash
printf 'header = "X-NuGet-ApiKey: %s"\n' "$ELPAQUETO_PUBLISH_KEY" \
  | curl -sS -K - -X POST https://nuget.example.test/api/v2/package/<PackageId>/<version>
```

The answer is `200` when the version exists. `PathBase`, if set, goes before `/api`.

## From the web UI

The **Upload** page of the web UI shows the commands for the common clients; it does not accept uploads itself. Publishing always goes through a client and an API key.

## Errors

| Status | Usual cause |
| --- | --- |
| `401` | Missing, wrong, revoked or expired key, or a key whose owner is disabled. All give the same answer; the server log has the reason. |
| `403` | The key lacks `Publish` (or `Admin` for a hard delete), its owner lost the permission, or the key was sent as the source password instead of `--api-key`. Also `HTTPS is required.` for plain HTTP from another machine. |
| `409` | The version exists and overwrites are not allowed. |
| `429` | Too many failed sign-ins from your address: see [troubleshooting](troubleshooting.md#429-too-many-requests). |

More in [troubleshooting](troubleshooting.md).
