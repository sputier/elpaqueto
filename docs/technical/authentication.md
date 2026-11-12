# Authentication

HTTP Basic authentication is implemented by `ElPaquetoBasicAuthenticationHandler` (`src/ElPaqueto.Security/Authentication/`). It replaces the handler inherited from BaGetter and is registered under the same scheme name, `NugetBasicAuthentication`, as default authenticate and challenge scheme, so the controllers that reference it did not change ([ADR 0002](../adr/0002-authentication-architecture.md), decision 3). The policy `NuGetUserPolicy` requires an authenticated user; the permission model and deny-by-default are separate changes.

## Flow of a request

1. **Parsing** (`BasicCredentialsParser`). No `Authorization` header, or another scheme, means "no result": a protected endpoint then answers with a challenge. A Basic header must be exactly `Basic`, one or more spaces, and strict base64 of UTF-8 `user:password`, at most 2048 characters, in one header value. Anything else fails: invalid base64, bytes that are not UTF-8, no colon, an empty user name, two `Authorization` values, an oversized header. The scheme is matched without regard to case. The split is on the first colon, so a password may contain colons.
2. **User lookup.** A user name must be 1 to 64 characters from `A-Z a-z 0-9 . _ -`; anything else is handled exactly like an unknown user. The user is read from the security database on **every request** (one indexed row), so a disable, a permission change or a password reset made by the command line applies to the next request.
3. **Verification.** Unknown users, disabled users and wrong passwords all end in the same generic failure, and the first two perform the same password-hashing work as a real verification ([password hashing](passwords.md)).
4. **Result.** The principal carries the user id, the user name, one claim per granted permission flag and the authentication method (`password`). The challenge is `401` with `WWW-Authenticate: Basic realm="ElPaqueto"`, an empty body and never a redirect. Failure messages are fixed strings that contain no request data.

No code path produces an authenticated principal for an anonymous request. With no users in the database, a request that carries credentials is refused as an unknown user.

The security database is only touched for a request that carries Basic credentials: a request without credentials is answered without reading or opening the file. The file is prepared once, at startup (opening and closing a file handle per request would drop the locks SQLite holds on it on POSIX systems, and would fail every route whenever the file is momentarily unavailable).

**If the database cannot be read** while a request carries credentials, the request fails with a server error (500) and the exception is logged: authentication fails closed, but it is not reported as a wrong password, so that an outage is visible instead of looking like bad credentials. Requests without credentials are unaffected.

## Verification cache

NuGet clients send credentials on every request and a password hash is deliberately slow, so a successful verification is remembered in memory for `Security:Authentication:VerificationCacheTtl` (default 5 minutes, `00:00:00` disables the cache, maximum one hour).

- There is **one entry per user**. It holds `HMAC-SHA256(per-process random key, stored password hash, normalized password)`, compared in fixed time. The password is not stored in any reversible form, and the value means nothing outside the process. Because the entry is per user and uses the normalized password, every Unicode spelling of the same password shares one entry: the size is bounded by the number of users and cannot be filled by someone holding a valid password.
- Because the stored hash is part of the digest, **a password change invalidates the entry by itself**.
- **Only successes are cached**, never failures. The size is bounded to 10 000 entries.
- Requests that arrive together on a cold cache may each verify the password once before the first result is cached; the throttling and capacity limits of later changes bound that work.
- Nothing about the user's state is cached: whether the user is active and which permissions it holds are read from the database on every request. A cache hit therefore only skips the password hash.
- A hash made with a lower cost or an older format is replaced by a fresh one after the first successful login. A failure to store it (for example a concurrent update) is logged and never fails the request.

## What is not covered yet

API keys (`X-NuGet-ApiKey`), throttling of failed attempts, HTTPS enforcement, per-route permissions and deny-by-default are separate changes. Until deny-by-default lands, the Razor pages and the publish endpoints are not protected by this handler: **do not expose the server to untrusted networks yet.**
