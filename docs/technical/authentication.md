# Authentication

Authentication is implemented by `ElPaquetoAuthenticationHandler` (`src/ElPaqueto.Security/Authentication/`). It accepts API keys and HTTP Basic user passwords. It replaces the handler inherited from BaGetter and is registered under the same scheme name, `NugetBasicAuthentication`, as default authenticate and challenge scheme, so the controllers that reference it did not change ([ADR 0002](../adr/0002-authentication-architecture.md), decision 3). The policy `NuGetUserPolicy` and the fallback policy are decided by the permission table: see [authorization](authorization.md).

## Which credential decides

Exactly one credential decides a request, chosen in this order ([ADR 0002](../adr/0002-authentication-architecture.md), decision 6):

1. **`X-NuGet-ApiKey` present** (sent by `dotnet nuget push` and `dotnet nuget delete`): the key alone decides and `Authorization` is ignored. An invalid, revoked or expired key, a key whose owner is disabled, or **two `X-NuGet-ApiKey` values**, fail authentication (401), even if valid Basic credentials are also present. A header with an empty value is present, so it fails too.
2. **`Authorization: Basic`**, parsed strictly (below). If the password has the exact API key token format (`ApiKeyToken.IsTokenFormat`, see [API keys](api-keys.md)), it is validated **as an API key only**: the user name must be non-empty (the parser refuses an empty one) and is otherwise ignored, so it can never select another user. Any other password is verified **as the user's password only**. No value is ever tried both ways: a token-shaped password is never looked up as a user password, and the password policy refuses passwords with that shape ([passwords](passwords.md)).
3. **Anything else** (no credentials, another scheme): no result, and a protected endpoint answers with the challenge.

Every failure, whichever credential and whatever the reason, gives the same 401: same status, same `WWW-Authenticate: Basic realm="ElPaqueto"`, same empty body (tested byte for byte against a wrong password, on a read route and on each write route).

A key builds this principal: the owner's user id and user name, the authentication method `api_key`, the key's public key id, where the key was read from (`x-nuget-apikey` for the header, `basic-password` for a Basic password), and one claim per **effective** permission: the key's permissions intersected with the owner's **current** permissions, recomputed by `IApiKeyService.ValidateAsync` on every request. Write routes accept only a key read from the header ([authorization](authorization.md)). The validation itself (constant-time comparison, revocation, expiry, disabled owner, debounced last-use timestamp) is described in [API keys](api-keys.md).

A refused key is logged at `Information` level by the handler, with the reason, the public key id (when the value had the token format) and where it was read from; never the token. Throttling of failed attempts is a separate change: the handler is structured so that it can be consulted before each validation and told about each failure, by client address and key id or user name.

## Password flow

1. **Parsing** (`BasicCredentialsParser`). No `Authorization` header, or another scheme, means "no result": a protected endpoint then answers with a challenge. A Basic header must be exactly `Basic`, one or more spaces, and strict base64 of UTF-8 `user:password`, at most 2048 characters, in one header value. Anything else fails: invalid base64, bytes that are not UTF-8, no colon, an empty user name, two `Authorization` values, an oversized header. The scheme is matched without regard to case. The split is on the first colon, so a password may contain colons.
2. **User lookup.** A user name must be 1 to 64 characters from `A-Z a-z 0-9 . _ -`; anything else is handled exactly like an unknown user. The user is read from the security database on **every request** (one indexed row), so a disable, a permission change or a password reset made by the command line applies to the next request.
3. **Verification.** Unknown users, disabled users and wrong passwords all end in the same generic failure, and the first two perform the same password-hashing work as a real verification ([password hashing](passwords.md)).
4. **Result.** The principal carries the user id, the user name, one claim per granted permission flag and the authentication method (`password`). The challenge is `401` with `WWW-Authenticate: Basic realm="ElPaqueto"`, an empty body and never a redirect. Failure messages are fixed strings that contain no request data.

No code path produces an authenticated principal for an anonymous request. With no users in the database, a request that carries credentials is refused as an unknown user.

The security database is only touched for a request that carries credentials (Basic or `X-NuGet-ApiKey`): a request without credentials is answered without reading or opening the file. The file is prepared once, at startup (opening and closing a file handle per request would drop the locks SQLite holds on it on POSIX systems, and would fail every route whenever the file is momentarily unavailable).

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

Throttling of failed attempts is a separate change: **do not expose the server to untrusted networks yet.**
