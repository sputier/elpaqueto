# Compatibility with BaGetter installations

What ElPaqueto keeps, refuses or reports when it starts on an installation made for BaGetter. The operator guide is [upgrading from BaGetter](../user/upgrading-from-bagetter.md).

## Kept

The package database schema, its migration ids, the storage layout and the configuration keys of the package server are unchanged: pointing `Database:ConnectionString` at an existing database is enough (`BaGetterDatabaseUpgradeIntegrationTests`).

## Refused: legacy authentication settings

`ApiKey`, `Authentication:Credentials:*` and `Authentication:ApiKeys:*` are rejected by `LegacyAuthenticationSettingsValidator` (`ElPaqueto.Security`), an `IValidateOptions<ElPaquetoOptions>`. It reads `IConfiguration`, not the bound options, so that the failure names the exact key path whatever the source; an empty value counts as absent. `Program.Main` validates `ElPaquetoOptions` before any command runs, so the server and the `admin` commands both stop with exit code 1. Failing instead of warning is deliberate: a warning would leave an operator believing the feed is protected by settings that do nothing, and ElPaqueto has no earlier release whose users need a grace period.

The inherited option classes stay in `ElPaqueto.Core` so that upstream changes still port mechanically ([upstream porting](../upstream-porting.md)). `Mirror:Authentication` is a different section and is still used. ASP.NET Core's own `Authentication:*` keys (such as `Authentication:DefaultScheme`) are not BaGetter's and are not rejected.

## Reported: renamed defaults

Both checks are in `BaGetterUpgradeChecks` (`src/ElPaqueto`) and only report; they never rename or open a file.

- **`BAGET_CONFIG_ROOT`.** Read when `ELPAQUETO_CONFIG_ROOT` is unset or empty, with a warning logged once the host is built (the configuration is loaded before logging exists). To be removed in ElPaqueto 2.0.
- **`bagetter.db`.** Before the package database migrations run (they would create the file), when `Database:Type` is `Sqlite`, the configured file does not exist and `bagetter.db` exists in the same folder, one warning names both full paths. The connection string is parsed with `SqliteConnectionStringBuilder` and never logged: it may hold a password. A relative data source is resolved against the current directory, as SQLite does; in-memory databases and `file:` URIs are skipped. Once the server has created the new file the warning stops, so it is logged at the first start only.

## Startup failures

`Program.Main` builds the host inside its `try`: building reads every configuration source, and an unreadable or malformed file (`IOException`, `UnauthorizedAccessException`, `FormatException`, `InvalidDataException` while building) is reported on standard error as `ElPaqueto could not load its configuration: <message>` with exit code 1. Only the outer message is printed: the inner exception of a JSON parse error can quote the file. Any other exception still ends with exit code 1 and the full exception, never an unhandled crash.

Without a `HealthCheck` section, the liveness endpoint is mapped at `/health` (`HealthEndpoints.DefaultPath`) instead of failing with a `NullReferenceException`.
