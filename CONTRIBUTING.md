## Contribute

Please read the [code of conduct] before contributing.

## Prerequisites

- The **.NET 10 SDK**. It includes the runtime. The exact SDK version is pinned in `global.json`. Run `dotnet --list-sdks` to see what is installed.
- [Node.js], only if you work on the documentation site.
- Docker, only if you build or test the container image.

If `dotnet` reports `A compatible .NET SDK was not found`, the installed SDK does not satisfy `global.json`: install the .NET 10 SDK from the [.NET download page][.NET SDK]. Do not edit `global.json` to work around it: local builds, CI and the container image must use the same SDK.

## Run from source

1. Clone the repository
2. Navigate to `src/ElPaqueto`
3. Start the service with `dotnet run`
4. Open the URL `http://localhost:5000/v3/index.json` in your browser

## Continuous integration

Every pull request and every push to `develop` or `main` runs the `CI` workflow on GitHub-hosted Ubuntu and Windows runners. It restores, builds in Release mode and runs the whole test suite, then publishes the TRX results as an artifact and fails when no test was executed. It needs no secret, so it also runs for pull requests from forks.

To reproduce it locally:

```bash
dotnet restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
```

A failing test must be fixed or explained in the pull request. Do not skip or disable it to make a check pass.

## Releases

No release automation exists yet: the inherited upstream workflows that published packages, Docker images and documentation were removed. Nothing is published automatically from this repository until a dedicated release workflow is added.

[Code of conduct]: CODE_OF_CONDUCT.md
[.NET SDK]: https://www.microsoft.com/net/download
[Node.js]: https://nodejs.org/
