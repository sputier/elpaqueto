# Manual checklist: Visual Studio and JetBrains Rider

The two IDEs cannot be driven by a test: their windows, credential dialogs and package browsers are graphical. What can be automated is automated (nuget.exe, and the `msbuild -t:restore` that Visual Studio runs: see [client compatibility](client-compatibility.md)). This checklist covers the rest, **once, against the real server over HTTPS behind your reverse proxy**, before you rely on an IDE. Nothing below has been run by the project: the matrix in [client compatibility](client-compatibility.md) shows Visual Studio and Rider as **not verified** until somebody does it and records the result.

Prepare:

- A user with `Read` and `Publish` (the IDEs only read; you push with the command line) and an API key with `Read` for the source credentials: [administration](administration.md). A key as the Basic password is the safest credential for an IDE: you can revoke it without changing a password.
- Two or three packages on the server, one with a readme (`<PackageReadmeFile>`), one with a prerelease version, one with a dependency. Push them with `dotnet nuget push` or `nuget.exe push` ([publishing](publishing.md)).
- The public HTTPS URL of the service index: `https://nuget.example.test/v3/index.json`. Test with HTTPS, as in production: the IDEs refuse plain HTTP unless the source allows it.

Record for each run: the IDE version (Visual Studio: Help > About; Rider: Help > About), the date, and what you saw. Mark each box only for what you observed.

## Visual Studio (2022 17.13 or later, 2026)

Source and credentials

- [ ] Tools > Options > NuGet Package Manager > Package Sources: add a source with the service index URL.
- [ ] Open Manage NuGet Packages and choose the source. A credential dialog appears **once**; after valid credentials (user name and password, or any user name and the API key as password) it does not come back during the session.
- [ ] Enter a wrong password: a clear failure message (`401`), not an endless loop of dialogs. Cancel the dialog: the source shows an error, other sources still work.
- [ ] The credentials are remembered across a restart of Visual Studio (Windows Credential Manager, or `%AppData%\NuGet\NuGet.Config`). If they are in `NuGet.Config`, the value is `Password` (encrypted), not `ClearTextPassword`. A secret in clear text is a finding: report it.

Browse and install

- [ ] Browse lists the packages; search finds one by name, and by part of the name.
- [ ] A package page shows its description, authors, dependencies, and its **icon**.
- [ ] The **readme** shows on the package page (Visual Studio 2022 17.13 and later, 2026, need `ReadmeUriTemplate/6.13.0` in the service index, which ElPaqueto now advertises: upstream [bagetter/BaGetter#289](https://github.com/bagetter/BaGetter/issues/289) did not). If it does not show, note the Visual Studio version: the service index and the readme URL are tested by the end-to-end tests, so the cause is in the client.
- [ ] Install a stable version into a project; the dependency is installed with it. Toggle **Include prerelease** and install a prerelease version.
- [ ] Update a package to a newer version; uninstall it.

Restore and build

- [ ] Close the solution, then Tools > Options > NuGet Package Manager > General > **Clear All NuGet Cache(s)**. Reopen it and build: packages restore from the server without a credential dialog (the stored credentials are used).
- [ ] A `packages.config` project (.NET Framework): Restore NuGet Packages works, and so does the build.
- [ ] Run the tests of a project that consumes a package in Test Explorer.

Refusals

- [ ] With a user that has no `Read`, the source shows a `403` message, not a loop of credential dialogs.
- [ ] A `NuGet.Config` next to the solution, with a `packageSourceCredentials` section for the source, is used without a dialog ([client setup](client-setup.md#visual-studio)).

## JetBrains Rider

Rider uses the NuGet client libraries and reads the same `NuGet.Config` files as the command line.

- [ ] Settings > Build, Execution, Deployment > NuGet > Sources: add the source URL, with the user name and password in the source's fields (or add the section to your user-level `NuGet.Config`, see [client setup](client-setup.md#jetbrains-rider)).
- [ ] The NuGet tool window lists the packages of the source and searches it; a wrong password shows an error, not a loop.
- [ ] Install, update and remove a package; a prerelease version appears with the prerelease box checked.
- [ ] Clear the caches (NuGet tool window > Clear NuGet caches, or `dotnet nuget locals all --clear`), then Restore: the packages come from the server.
- [ ] Look at where Rider stored the password (`NuGet.Config`, clear text or encrypted). Report it if it is in a file readable by other users.
- [ ] The readme of a package: note whether Rider shows it (it may not: that would be a client limitation, not a server one).

## Reporting the result

Open an issue (or a pull request on [client compatibility](client-compatibility.md)) with the IDE and its version, the operating system, the date, and for each failed box the exact message. A server-side cause shows in the server's [log](logging.md): look for the `401`, `403` or `429` of the same minute.
