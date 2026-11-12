# Password hashing

Passwords are hashed by `PasswordService` (`src/ElPaqueto.Security/Passwords/`), a thin layer over ASP.NET Core Identity's `PasswordHasher<TUser>`. ElPaqueto implements no cryptography of its own.

## Algorithm and parameters

- Format: Identity **V3**. The hash records its own algorithm, iteration count and salt size, so the cost can be raised later without invalidating existing hashes.
- Key derivation: PBKDF2 with **HMAC-SHA512**, a random 128-bit salt and a 256-bit derived key.
- Iterations: `Security:Passwords:IterationCount`, default **210000** (OWASP Password Storage Cheat Sheet). The framework default is 100000.
- A test decodes a fresh hash to assert the V3 marker, the HMAC-SHA512 identifier and the configured iteration count, and verifies two hashes committed in the test project (one at the default cost, one at 100000 iterations) so that a framework update changing the format fails the build instead of locking users out.

## Normalization and limits

Passwords are normalized to Unicode **NFKC** before hashing and verifying (NIST SP 800-63B), identically on both paths, so a password typed on different systems verifies. Input is cut at 1024 characters before normalization (NFKC can lengthen a string); the policy limit of 256 characters is applied after it. A password longer than the limit is never hashed in full.

## Verification

`Verify(user, password)` returns `Failed`, `Success` or `SuccessRehashNeeded`.

- `SuccessRehashNeeded` means the password is correct but its hash uses a lower iteration count or an older format. The caller hashes the password again and stores the new hash. (The authentication handler that will do this after a successful login is not part of this change.)
- **Unknown, disabled or hash-less user, or over-length password:** the submitted password is verified against a dummy hash created once at startup with the current parameters, then `Failed` is returned. The amount of work equals the known-user path, so response times do not reveal which user names exist. Tests assert this by counting calls to the hasher, not by timing.

## Policy

Applied by `Validate` when a password is set (never at login): between `Security:Passwords:MinimumLength` (default 15, floor 12) and 256 characters after normalization; not equal to the user name, ignoring case; not in the [API key token format](api-keys.md) (`epq_` + 12 + `_` + 43 base62 characters), checked on the password as entered and after normalization, because a Basic password with that format is validated as an API key only and could never be used as a password. There are no composition rules. Messages never contain the password.

## Cost

Every NuGet request carries Basic credentials, and a restore can issue hundreds of requests, so the per-verification cost matters. Measured on this code at 210000 iterations (informational, from `PasswordServiceTests.VerificationCost_AtTheDefaultIterationCount_IsMeasured`):

| Environment | Time per verification |
| --- | --- |
| Windows 11 workstation | about 86 ms |
| Linux container (`mcr.microsoft.com/dotnet/sdk:10.0`, 8 CPUs) | about 129 ms |

Limiting the amount of hashing per request (a verification cache) and the rate of failed attempts (throttling) is the job of later changes. Until they land, the cost is the only lever: it is configurable down to 100000 iterations for small hosts.

## What is not logged

Passwords are never logged, never placed in exception messages and not kept longer than needed. A test captures all logging and messages while hashing, verifying and validating a sentinel password and asserts it appears nowhere.
