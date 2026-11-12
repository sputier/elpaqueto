# Changelog

All notable changes to ElPaqueto are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The release procedure is in
[docs/maintainers/releasing.md](docs/maintainers/releasing.md).

ElPaqueto is a fork of [BaGetter](https://github.com/bagetter/BaGetter). Changes made upstream before
the fork are not listed here.

## [Unreleased]

No version of ElPaqueto has been released yet. The first release will contain the changes below.

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

### Removed

- Inherited upstream release, documentation deployment and Helm workflows (#2, #54).
- Stale backup test project (#4, #57).
