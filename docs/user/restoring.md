# Restoring packages

Restore needs a user password or an API key with `Read`, given as the source credentials (HTTP Basic). Set up the source and the credentials as described in [client setup](client-setup.md), then:

```bash
dotnet restore
```

The client sends the credentials on every request to the source. Over anything but your own machine, the source must be `https://`: ElPaqueto refuses plain HTTP from other machines (`403 HTTPS is required.`).

## Caches

NuGet keeps two caches that can hide what the server currently answers:

- the **HTTP cache** of service index, registration and version responses (about 30 minutes);
- the **global packages folder**, where restored packages are extracted (`~/.nuget/packages`, or `NUGET_PACKAGES`).

A package restored once is taken from the global packages folder without asking the server, even after its key was revoked. To check what the server answers now:

```bash
dotnet restore --no-http-cache          # ignore the HTTP cache for this restore
dotnet nuget locals http-cache --clear  # or clear it
dotnet nuget locals all --clear         # clear every cache, the global packages folder included
```

## Versions

- **Exact versions** (`Version="1.2.3"`) restore whether the version is listed or [unlisted](publishing.md#unlist-and-delete).
- **Floating versions** (`Version="1.*"`, `Version="1.2.*-*"`) and minimum versions resolve against the versions the server lists; NuGet prefers listed versions.
- **Prerelease versions** (`1.0.0-beta.1`) need an explicit prerelease version or a floating range with a prerelease part, as with any NuGet feed.
- A version deleted with `PackageDeletionBehavior: HardDelete` no longer restores.

## Mirror

When `Mirror:Enabled` is `true`, a package that is not in ElPaqueto is fetched from `Mirror:PackageSource` (nuget.org by default) the first time a client asks for it, and stored. The client still authenticates to ElPaqueto with `Read`; ElPaqueto authenticates to the upstream source with `Mirror:Authentication` if configured. The server then needs outbound Internet access.

## Errors

See [troubleshooting](troubleshooting.md): `401` and `NU1301` for credentials, `NU1302` for a plain-HTTP source, `429` for throttling.
