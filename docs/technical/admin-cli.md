# Administration commands

The `admin` command group lives in `src/ElPaqueto.Security/Cli/`. User-facing behaviour is documented in [Administration from the command line](../user/administration.md).

## Wiring

`Program.cs` builds the full host, validates the options, then hands the arguments to a McMaster `CommandLineApplication`. The security module adds its commands with one call, `app.AddElPaquetoCommands(host)` (`ElPaquetoCommandLineExtensions`). The commands resolve their services from `host.Services` in a scope per execution. The host is built but **never started**: no hosted service runs (in particular not `AuthDatabaseStartupService`), the package database is not migrated and Kestrel does not bind. Each command that touches the security database calls `IAuthDatabaseMigrator.MigrateAsync` itself, after its input was validated, so a usage error never creates the database file.

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

## Logging

State-changing operations log one structured event through `UserAdministrationLog` (`LoggerMessage` source generator, category `ElPaqueto.Security.Administration`):

| Event id | Name | Level | Fields |
| --- | --- | --- | --- |
| 2001 | `AdministratorBootstrapped` | Information | `UserName`, `UserId` |
| 2002 | `BootstrapRefused` | Information | `UserName`, `Reason` |
| 2099 | `AdminCommandFailed` | Error | `Command`, exception |

Passwords and hashes are never fields. EF Core does not log parameter values (sensitive data logging stays off). A sentinel test captures every log at `Trace` level and every output stream and asserts that neither the password nor the stored hash appears.

## Tests

- `tests/ElPaqueto.Security.Tests/Cli/AdminCli.cs` runs the command tree in process over a real SQLite file, with a string-backed `IConsole`, a fake prompt and captured logs.
- `BootstrapCommandTests` covers the acceptance criteria and the abuse cases (password options, misplaced values, invalid names, oversized or non-UTF-8 password files, concurrency).
- `AdminCommandProcessTests` runs the built `ElPaqueto.dll` with real standard input (`HostProcess` always redirects and closes it), checks exit codes, that no package database file is created and that no port is bound (the port the server would use is held by the test, so a Kestrel start would fail the command), and that a bootstrap succeeds while the server runs on the same data.
