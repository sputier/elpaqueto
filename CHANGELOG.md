# Changelog

All notable changes to ElPaqueto are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The release procedure is in
[docs/maintainers/releasing.md](docs/maintainers/releasing.md).

ElPaqueto is a fork of [BaGetter](https://github.com/bagetter/BaGetter). Changes made upstream before
the fork are not listed here.

## [Unreleased]

### Added

- On-demand development images of `develop` or `main` on the GitHub Container Registry, tagged with the
  branch and the commit, validated like a release and never tagged `latest` (#135).

## [0.1.0] - 2026-11-18

First release of ElPaqueto: a hardened fork of BaGetter, published as a container image on the GitHub
Container Registry (`ghcr.io/sputier/elpaqueto`) for `linux/amd64` and `linux/arm64`. Compared with
BaGetter, every request is authenticated and authorized by default, the container is hardened, and the
security behaviour is covered by automated tests.

### Added

- Security database (SQLite) with users, permissions and migrations applied at startup (#15, #78).
- Password hashing with a password policy (#16, #79).
- HTTP Basic authentication of users against the security database (#17, #81).
- Deny-by-default authorization with a route-to-permission table (#24, #82).
- API keys: model, token format and storage (#31, #83); authentication with keys, including keys
  given as Basic passwords, and writes that require the key header (#32, #88).
- Administration command line: first-administrator bootstrap (#20, #84), user management (#21, #86)
  and API key commands (#33, #95).
- Throttling of failed sign-ins and bounded password verifications (#18, #90).
- Trusted forwarded headers, public base URL and HTTPS requirement (#19, #85).
- Validated `Security` configuration options; invalid configuration stops the process (#12, #74).
- Security audit logging with secret redaction (#22, #96).
- Hardened container image, Compose example, reverse-proxy guidance and container CI with a smoke
  test and a vulnerability scan (#44, #91).
- Release workflow that publishes the container image to the GitHub Container Registry (#47).
- End-to-end tests with the published server and the real `dotnet` CLI on Linux and Windows (#36–#40,
  #101).
- Client compatibility page listing the NuGet clients that are tested, and how (#52).
- User and technical documentation, including the known limitations (#98, #109).
- End-to-end tests with the real nuget.exe and `msbuild -t:restore` on Windows, client setup for
  nuget.exe, Visual Studio and Rider, and a manual checklist for the IDEs (#43).
- The service index advertises `ReadmeUriTemplate/6.13.0`, so that Visual Studio shows the readme of a
  package (#43; upstream bagetter/BaGetter#289).
- PostgreSQL package database tested against a real PostgreSQL 17 in CI, and a Compose example with
  PostgreSQL and Caddy, secrets passed as files (#128).

### Changed

- Renamed from BaGetter to ElPaqueto (#58, #60).
- Targets .NET 10, with updated dependencies (#1, #56).
- Hardened CORS and response headers, and the web UI (#27, #29, #89).
- The security database is hardened (#80).
- Legacy BaGetter authentication settings are retired; upgrades from BaGetter are checked at startup
  (#35, #94).

### Fixed

- A `dotnet restore` or `dotnet nuget push` with a stale password no longer trips the sign-in throttle by
  itself: a repeated identical failed attempt is counted once, answered without hashing, and logged at
  `Debug`, so the client reports `401` and the right password works right away; distinct wrong passwords
  are still throttled (#99).
- PostgreSQL: a `Version` column left case-sensitive by an old BaGet database is repaired at startup
  (upstream bagetter/BaGetter#239), and two servers starting on one empty database no longer apply the
  same migration twice (#128).

### Removed

- Inherited upstream release, documentation deployment and Helm workflows (#2, #54).
- Inherited BaGetter Helm chart (`deployment templates/`): it deployed the upstream image without any of
  ElPaqueto's security settings (#51).
- Stale backup test project (#4, #57).
- Inherited BaGetter documentation site (Docusaurus) and its Node.js dependencies; its still-accurate
  content (mirror authentication, retention, importing packages) moved to the user documentation (#51).
