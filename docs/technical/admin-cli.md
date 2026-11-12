# Administration commands

The `admin` command group lives in `src/ElPaqueto.Security/Cli/`. User-facing behaviour is documented in [Administration from the command line](../user/administration.md).

## Wiring

`Program.cs` builds the full host, validates the options, then hands the arguments to a McMaster `CommandLineApplication`. The security module adds its commands with one call, `app.AddElPaquetoCommands(host)` (`ElPaquetoCommandLineExtensions`). The commands resolve their services from `host.Services` in a scope per execution. The host is built but **never started**: no hosted service runs (in particular not `AuthDatabaseStartupService`), the package database is not migrated and Kestrel does not bind. Each command that touches the security database calls `IAuthDatabaseMigrator.MigrateAsync` itself, after its input was validated, so a usage error never creates the database file.

`Program.cs` also calls `ConfigureElPaquetoCommandLogging(args)` on the host builder: when any argument is `admin` (wherever root options put the command), it sets `ConsoleLoggerOptions.LogToStandardErrorThreshold` to `Trace`, so every console log line goes to standard error and standard output only carries results. This is what makes `admin key create ... > key.txt` safe at any log level; the options must be set before the host is built, which is why it is not done in `AddElPaquetoCommands`. `ApiKeyCommandProcessTests` runs `create` with every category, Entity Framework Core included, at `Information` and asserts that standard output is exactly the key; removing the call fails it.

`Program.cs` ignores the value returned by `ExecuteAsync`, so the commands set `Environment.ExitCode`. Tests use an internal overload that reports the exit code to a delegate instead, so the test process's own exit code is never touched.

## Argument parsing

The `admin` commands declare no options to McMaster. Each group and command uses `UnrecognizedArgumentHandling.CollectAndContinue` and parses the collected arguments with `CommandLineSyntax`:

- McMaster throws on a missing option value; `Program.cs` would turn that into exit code 1 and a stack trace. Here every syntax error is exit code 2.
- McMaster repeats unknown arguments in its errors. An unknown argument may be a password typed in the wrong place (`--password secret`, or a value without its option), so `CommandLineSyntax` never quotes an argument back: it reports a position and the list of valid options. For the same reason, password-file errors do not repeat the path. The only names it prints are its own constants (`--password`, `-p`, ... get an explanation of the password sources).
- `--name value` and `--name=value` are accepted; a value starting with `--` is treated as a missing value; an option given twice is an error.

`--help` is still McMaster's inherited help option. The command arguments and options are listed in the extended help text.

## Password input

`PasswordInput` reads the password from `--password-file`, `--password-stdin` or, only when `IConsole.IsInputRedirected` is false, from `ISecretPrompt` (twice, compared ordinally). Input is bounded (1024 characters; a file is read up to 8 KiB plus one byte, whatever its reported size, so pipes and devices are bounded too) before the policy check of `IPasswordService.Validate`. The real prompt (`ConsoleSecretPrompt`) reads keys with `Console.ReadKey(intercept: true)` and writes the prompt to standard error. Tests replace `ISecretPrompt` in the container.

## Bootstrap and transactions

`UserService.BootstrapAsync` validates the user name with `UserNames` (`ElPaqueto.Security.Authentication`), the rule the Basic handler applies, and the password policy, hashes the password, then opens the connection and starts the transaction with `SqliteConnection.BeginTransaction(deferred: false)`, which issues `BEGIN IMMEDIATE`. The write lock is held from before the "does a user with `Admin` exist?" query until the commit. With WAL and the busy timeout set by `AuthDatabaseConnectionInterceptor`, a second process blocks on `BEGIN IMMEDIATE` until the first commits, then sees the new administrator and exits with code 3. The unique index on `NormalizedUserName` is the backstop for a duplicate name; a `SQLITE_CONSTRAINT_UNIQUE` error is reported as a taken name, not as a crash.

A deferred transaction would not be enough: two processes could both read "no administrator" and the second one would fail with `SQLITE_BUSY` on its write, or, without any transaction, both would succeed. `BootstrapCommandTests.TwoConcurrentBootstraps_ExactlyOneSucceeds` stalls the administrator check with a command interceptor to make the race certain; it fails with both variants. `AdminCommandProcessTests` repeats the check with two real processes.

## User changes

`UserService.ChangeAsync` serves `disable`, `enable`, `set-permissions` and `reset-password`. Inside one `BEGIN IMMEDIATE` transaction it loads the user, applies the change, and, when the user was an active administrator and no longer is, counts the *other* active users holding `Admin`; if there is none it rolls back and reports `LastActiveAdministrator` (exit code 3). A no-op (already in the requested state) writes nothing and keeps `UpdatedAt` and `ConcurrencyStamp`. Every write sets `UpdatedAt`; the context regenerates `ConcurrencyStamp`. Because the read and the write share the write lock, a concurrent writer cannot interleave; a `DbUpdateConcurrencyException` is still mapped to exit code 1 with a "retry" message. `UserCommandTests.TwoConcurrentDisables_OfTheOnlyTwoActiveAdministrators_LeaveOneActive` stalls the count query to force the race; without the transaction both disables succeed and the test fails.

JSON output is written field by field with `Utf8JsonWriter` from `UserSummary`, a record without a password hash, so no serializer setting can add one.

## API key commands

`ApiKeyCommands` (`admin key create|list|show|revoke|rotate`) is a thin layer over `IApiKeyService` ([API keys](api-keys.md)); it does not touch the `ApiKeys` table itself. Before calling the service, it checks what it can report precisely, so each refusal gets its exit code and a message in command-line terms (`read, publish` rather than the enum names):

| Check | Where | Exit code |
| --- | --- | --- |
| user name, key name (`ApiKeyService.NameError`, the service's own rule), permission list, expiry choice and value, `--format`, key id format (`ApiKeyService.IsKeyIdFormat`) | before the database is opened, so a usage error never creates it | 2 |
| unknown owner, unknown key id | `UserService.FindAsync`, `IApiKeyService.FindAsync` | 4 |
| disabled owner, permission not held by the owner, rotation of a revoked key or of a key whose owner lost one of its permissions | read before the call | 3 |

The service checks all of these again: an `ApiKeyRequestException` thrown because something changed between the read and the write (a concurrent `user disable`, `set-permissions` or `key revoke`) is reported with exit code 3 and the service's message, never a token. `IApiKeyService.FindAsync(keyId)` was added for `show` and `rotate`; like `ListAsync` it returns `ApiKeyInfo`, which has no hash.

`--expires` is parsed by `ExpiryText`: `<n>d` (1 to 36500, added to the injected `TimeProvider`'s now), `yyyy-MM-dd` (00:00 UTC), or `yyyy-MM-ddTHH:mm[:ss]` with `Z` or an offset. A time without an offset is refused rather than read in the machine's time zone. Exactly one of `--expires` and `--no-expiry` is required, including for `rotate`.

**The token.** `WriteToken` is the only code that writes a token: to standard output, once, followed by `\n` (not `Environment.NewLine`, so a file written on Windows has the same bytes), or inside the JSON object of `--format json`. The summary and the "store it now" warning go to standard error. The token is never passed to a logger, an exception message or another command's output. A key id argument that looks like a complete key (`epq_` prefix) is refused without being repeated; so is such a value given to `--user` (unknown user) or `--permissions` (an unknown permission is only repeated when it has at most 16 characters and no `epq_` prefix), and a key name starting with `epq_` is refused. If writing the token fails (for example a closed pipe), the command exits with code 1 and names the new key id so that it can be revoked; the token is written before the log event, so nothing else can fail between storing the key and handing it over. `ApiKeyCommandTests.TheToken_NeverReachesTheLogs_OtherCommands_OrTheDatabaseFile` captures every log at `Trace`, the output of `list`/`show`/`revoke`, and the bytes of the database file and its WAL (ASCII and UTF-16), and finds no secret; the same scan finds the key ids.

`list` and `show` compute a `status` (`revoked`, `expired`, `owner-disabled`, `active`, in this order) and the effective permissions (key ∩ owner's current permissions) from the key and its owner, with the injected clock, exactly as validation does. `list` hides revoked keys unless `--include-revoked` is given; expired keys are listed.

## Logging

State-changing operations log one structured event through `UserAdministrationLog` (`LoggerMessage` source generator, category `ElPaqueto.Security.Administration`):

| Event id | Name | Level | Fields |
| --- | --- | --- | --- |
| 2001 | `AdministratorBootstrapped` | Information | `UserName`, `UserId` |
| 2002 | `BootstrapRefused` | Information | `UserName`, `Reason` |
| 2003 | `UserCreated` | Information | `UserName`, `UserId`, `Permissions` |
| 2004 | `UserDisabled` | Information | `UserName`, `UserId` |
| 2005 | `UserEnabled` | Information | `UserName`, `UserId` |
| 2006 | `UserPermissionsChanged` | Information | `UserName`, `UserId`, `OldPermissions`, `NewPermissions` |
| 2007 | `UserPasswordReset` | Information | `UserName`, `UserId` |
| 2008 | `LastActiveAdministratorProtected` | Information | `UserName` |
| 2009 | `ConcurrentChangeRejected` | Warning | `UserName` |
| 2010 | `ApiKeyCreated` | Information | `KeyId`, `UserName`, `UserId`, `Permissions`, `ExpiresAt` |
| 2011 | `ApiKeyRevoked` | Information | `KeyId`, `UserName`, `UserId` |
| 2012 | `ApiKeyRotated` | Information | `OldKeyId`, `NewKeyId`, `UserName`, `UserId`, `ExpiresAt` |
| 2099 | `AdminCommandFailed` | Error | `Command`, exception |

Passwords, hashes and API key tokens are never fields; a key is only named by its public key id. The service also logs its own events (category `ElPaqueto.Security.ApiKeys.ApiKeyService`). There is no separate audit trail yet: these events are the record of key administration. EF Core does not log parameter values (sensitive data logging stays off). A sentinel test captures every log at `Trace` level and every output stream and asserts that neither the password nor the stored hash appears.

## Tests

- `tests/ElPaqueto.Security.Tests/Cli/AdminCli.cs` runs the command tree in process over a real SQLite file, with a string-backed `IConsole`, a fake prompt and captured logs.
- `BootstrapCommandTests` and `UserCommandTests` cover the acceptance criteria and the abuse cases (password options, misplaced values, invalid names, oversized or non-UTF-8 password files, concurrency).
- `ApiKeyCommandTests` runs every `admin key` command in process: exactly one token line on standard output and none on standard error, validation of the created token with `IApiKeyService.ValidateAsync` and its permissions, refusal after `revoke` and of the old token after `rotate`, every refusal with its exit code (missing or invalid options and expiries, unknown or disabled user, permission not held, unknown or malformed key id, a complete key given as key id, revoked key or demoted owner on rotate), JSON parsed with `System.Text.Json` for every command, and the token scan above.
- `ApiKeyCommandProcessTests` runs the commands as child processes of the built `ElPaqueto.dll` against the security database of a running server and uses the keys over HTTP: a created key authenticates in `X-NuGet-ApiKey` and as a Basic password, a write passes authorization only with a `Publish` key in the header, a revoked key and the old key of a rotation get `401` at the next request, a key never exceeds its owner's permissions (refused at creation, restricted at use after `set-permissions`), and a key stops working when it expires. `HostProcess.StandardOutput` keeps standard output apart for these assertions.
- `AdminCommandProcessTests` runs the built `ElPaqueto.dll` with real standard input (`HostProcess` always redirects and closes it), checks exit codes, that no package database file is created and that no port is bound (the port the server would use is held by the test, so a Kestrel start would fail the command), and that a bootstrap succeeds while the server runs on the same data.
- `tests/ElPaqueto.Tests/AdminCommandAuthenticationIntegrationTests.cs` runs the `admin` commands with the built server binary as a child process (`AdminCommandLine`) and then sends Basic requests to the real server (`ElPaquetoApplication`) sharing the same security database. `v3/index.json` needs `Read` and `health/details` needs `Admin`, so `401` means refused credentials and `403` a missing permission. The bootstrapped administrator gets `200` on both; a wrong password, another name or no credentials get `401`, including when the server was already running before the bootstrap; a 64-character name using every allowed character class signs in. A user created as `reader` gets `200` on the index and `403` on the details. After `disable` the next request of the same client gets `401`, after `enable` `200` again. After `set-permissions` to `publish` the next request gets `403`, and after `read,admin` `200` on both. After `reset-password` the old password gets `401` (although it is in the verification cache) and the new one `200`; a disabled user stays rejected.
