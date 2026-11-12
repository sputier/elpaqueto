# Sign-in throttling

Failed sign-in attempts and concurrent password verifications are limited by `LoginThrottle` (`src/ElPaqueto.Security/Throttling/`), an in-memory service that the authentication handler consults **before** it looks the user up or verifies anything. Settings: [`Security:Throttling`](../user/configuration.md#securitythrottling).

## Threat model

The server is meant to be reachable from the Internet behind a TLS-terminating proxy, with no other infrastructure. The throttle addresses:

- **Online password guessing** against one account, from one client or from many.
- **Password spraying**: a few common passwords against many user names from one client.
- **User enumeration**: learning which user names exist from the answers. The throttle never knows whether a user exists: unknown, disabled and existing user names are counted identically, so its decisions cannot reveal it. Its answers (429, 503) are the only ones that differ from the uniform `401`, and they depend only on the attempts, never on the account.
- **CPU exhaustion** with deliberately failing verifications: each verification costs a PBKDF2 hash ([password hashing](passwords.md)). Throttled attempts cost no hash at all, and the number of hashes running at once is bounded.
- **Lockout of a legitimate user by an attacker** (denial of service). This is the constraint that shapes the design: a counter that refuses the account's owner whatever its address would let anyone lock out anyone, including CI accounts, by sending wrong passwords.
- **Lockout of a legitimate user by its own client.** NuGet clients retry a refused request with the same credentials (one `dotnet restore` sends six credentialed attempts): a stale password must produce `401`, not a throttle. See [repeated identical attempts](#repeated-identical-attempts).

Not addressed: distributed counters, CAPTCHA, permanent lockout requiring an administrator, IP allow or deny lists. Operators who need more can add limits at the proxy.

## Counters

| Counter | Key | Default | Refuses |
| --- | --- | --- | --- |
| Client | client address: the IPv4 address, or the IPv6 /64 network | 30 failures in 10 minutes | every attempt from that client |
| Network | IPv6 /48 network (IPv4 clients have none) | 300 failures in 10 minutes | every attempt from that /48 |
| Pair | (user name, client) | 5 failures in 15 minutes | that user name from that client |
| Repeat | client address, as for Client | 10 × `ClientFailureLimit` (300) repetitions of failures already counted, in 10 minutes | every attempt from that client ([repeated identical attempts](#repeated-identical-attempts)) |
| Account | user name, known or not | 50 failures in 1 hour | that user name, **only from clients with a failure for it in the last pair window** |

- **Why the account counter only refuses clients that failed.** A distributed attacker cannot be stopped per client, so a per-account ceiling is needed; but a ceiling that refused everyone would hand the attacker a lockout. Once the ceiling trips, every client that has guessed wrong for that user name recently is refused, and each new attacking address gets one guess before it is refused too. The owner, signing in from an address with no recent failure for the account, is never refused by this counter. If the owner did mistype from that address, it is refused until its own pair failure is older than the pair window (15 minutes by default) or until the account throttle ends, whichever is first.
- **IPv6 by /64, and by /48.** One subscriber usually gets a whole /64; counting single IPv6 addresses would let one client rotate through billions of them. A site, a rented server or a tunnel broker account often gets a /48, which holds 65536 /64 networks: the network counter keeps rotating through them from multiplying the budget. Its limit is higher than the client limit, because one /48 can hold many legitimate clients (an office, a campus). IPv4 addresses seen through a dual-stack socket (`::ffff:a.b.c.d`) are unmapped and counted as IPv4, never grouped. A request without an address (a Unix socket) is counted in one shared bucket.
- **User names** are normalized like the stored ones (upper-case invariant) and kept as a 128-bit prefix of their SHA-256: a long or random user name costs the same few bytes as any other, and no user name is kept in clear in memory by the throttle. Trip events never name a user; the sign-in failures that lead to them do (audit event 1002, see [audit logging](audit-logging.md)).

## Algorithm

Each counter keeps the times of its failures in a sliding window. A counter that reaches its limit **trips**:

- It refuses for a duration that starts at `InitialThrottleDuration` (1 minute) and doubles with each further trip while the counter is remembered (1, 2, 4, 8 minutes...), up to `MaximumThrottleDuration` (15 minutes). There is no permanent lockout.
- Its failures are cleared: after the throttle ends, the client gets a new budget, and the next trip lasts longer.
- A counter is forgotten (its trip count with it) one window after its last failure or the end of its last throttle, whichever is later.

Times come from the monotonic clock (`TimeProvider.GetTimestamp`), not the wall clock: a clock adjustment neither extends a throttle beyond its maximum nor ends every throttle at once.

For each request with Basic credentials, the handler:

1. Takes the client address left by the trusted forwarded-headers middleware ([forwarded headers](forwarded-headers.md)): `X-Forwarded-For` is only honoured from a trusted proxy, so a client cannot choose its own address to rotate out of its counters.
2. **Checks** the counters. A refused attempt gets `429` immediately: no database read, no hash, so a correct password is not accepted either.
3. Reads the user and checks the [verification cache](authentication.md#verification-cache). A hit is a success and needs no verification slot.
4. Otherwise **enters a verification slot**:
   - Attempts for the same (user name, client) run **one at a time**, and a client never has more verifications in flight than its remaining failure budget. A burst of parallel guesses from one client therefore cannot overshoot the client or pair limits (the network and account limits can be overshot by the attempts already in flight from different clients, which the global limit below bounds), and a burst of parallel requests with the right password on a cold cache runs one hash, the others finding the result in the cache once they get their turn. Waiting attempts are bounded by `VerificationQueueLimit`; beyond it the answer is `503`.
   - A global `ConcurrencyLimiter` (`System.Threading.RateLimiting`) bounds the hashes running at once to `MaxConcurrentVerifications` (default: the processor count), with a queue of `VerificationQueueLimit`; beyond it the answer is `503`.
5. Reports the outcome. A failure (wrong password, unknown user, disabled user) counts on every counter that applies, **once per identical attempt** (see below). A success resets **only the pair**: a valid account must not refill a client's budget for other user names, and the owner signing in must not refill a distributed attacker's budget against its account. An aborted request counts nothing.

Malformed `Authorization` headers, and requests with two `X-NuGet-ApiKey` values, are refused before the throttle and are not counted: they cost no verification.

## Repeated identical attempts

Clients retry. The `dotnet` CLI sends every refused request six times with the same stored credentials, without waiting ([what the real clients do](e2e-tests.md#what-the-real-clients-do)): counted one by one, a single restore with a stale password would exceed the default `PairFailureLimit` (5) by itself, the client would report `429` instead of the real cause, and the right password would then be refused for the throttle duration ([#99](https://github.com/sputier/ElPaqueto/issues/99)). So **a failed attempt is counted once**: repeating it in the window counts on no counter.

**Why this is safe.** Guessing needs distinct candidates. Repeating a wrong password, or a wrong key, that has already failed tells the sender nothing it does not know. Every *distinct* attempt still counts:

| Attempt, after a counted failure | Counted? |
| --- | --- |
| The same password, same user name (any case), same client, same account state | No |
| Another password | Yes: N distinct wrong passwords still trip the pair at `PairFailureLimit` |
| The same password for another user name (password spraying) | Yes, for each user name, on the client counter too |
| The same password from another client (another IPv4 address, another IPv6 /64) | Yes |
| The same password after the account changed (password reset, user enabled or disabled) | Yes: it is verified again |
| The same API key token (header or Basic password, same client) | No |
| Another token | Yes |

**Fingerprint.** What is remembered is `HMAC-SHA256(per-process random key, client key | user name digest | credential state | secret)`, truncated to 128 bits: never the secret, nothing meaningful outside the process, never logged. The client key is the one of the counters (IPv4 address or IPv6 /64); the user name digest is the one of the counters (normalized user name), absent for an API key, so a password and a key with the same value are different attempts. The **credential state** is what else decides the verification: the stored hash of an active user, or one fixed value of the same length for every unknown or disabled user name. Unknown, disabled and existing user names are therefore fingerprinted and de-duplicated identically.

**Window.** A fingerprint is remembered from its first counted failure for the longest of `ClientWindow`, `PairWindow` and `AccountWindow` (1 hour by default), and repetitions do not extend it: an identical failure counts at most once per window, so it never counts again while its first occurrence still counts on any counter.

**Concurrency.** The fingerprint is looked up and recorded under the throttle's lock when the outcome is reported: of parallel identical failures, exactly one counts. Password attempts for one (user name, client) already run one at a time, so a repetition is recognised before it is verified.

**Cost of a repetition.** A repeated password for an unchanged account state would fail again: it is answered `401` **without hashing**, so retries cost no processor time (a repetition that cost a hash and counted nothing would let one client keep the verification capacity busy indefinitely). The answer is delayed by the typical duration of a verification measured on the server (a moving average of the samples, each counted as at most 2 seconds), without holding a verification slot or a processor, so that the response time is not a cheap signal of whether an attempt was verified or recognised as a repetition, which would reveal that an account's state changed between two identical attempts. The delay is an average and does not reproduce the jitter of a real verification; learning a state change that way needs a new counted failure per sample. A repeated API key is still validated (one indexed read and a SHA-256: the key or its owner may have changed), but not counted. Repetitions are logged at `Debug` (events 1002 and 1003); the first failure is logged at `Warning` as before ([audit logging](audit-logging.md)).

**Repetitions are bounded too.** Each repetition costs a database read, an HMAC and a log line at `Debug`. A separate per-client counter, `Repeat` (client key, `ClientWindow`), counts them with a limit of ten times `ClientFailureLimit` (300 in 10 minutes by default, against 6 per NuGet command): beyond it the client is throttled like any other (`429`, trip logged as audit event 1801 with the kind `Repeat`), with the same doubling durations. Repetitions count on no other counter.

**Lookup timing.** The fingerprint is computed for every attempt, known user name or not, before the lock is taken, from inputs of the same shape; the lookup is a hash-set lookup on a fixed-size value. Neither depends on whether the user exists.

**Bounded memory, fail closed.** The store holds at most `MaxTrackedEntries` fingerprints (about 60 bytes each; 3 MB with the default). Expired fingerprints are dropped first. When it is still full, a new failure is **counted and not remembered**, and so are its repetitions: filling the store can only make failures count as they did before this mechanism, never less. Filling it requires `MaxTrackedEntries` distinct counted failures in one window (one hour by default), which the client limits bound per address but not across many addresses: an attacker with enough addresses (many IPv6 /48 networks, a botnet) can fill it, and legitimate clients retrying a stale password then trip their pair throttle again, as before #99. The de-duplication is therefore best-effort under a distributed attack; it never weakens the limits.

**Defaults.** With repetitions counted once, a `dotnet restore` with a stale password counts one failure; the default limits (5 per pair, 30 per client, 50 per account) are kept: they bound distinct guesses, which is what they are for.

## API keys

A key, whether sent in `X-NuGet-ApiKey` or as a Basic password, has no user name to count against (the user name sent with a key is ignored): the handler calls the throttle with no user name, so a key attempt counts **against the client and its IPv6 /48 network only**.

1. **Check** before the key is parsed or looked up: a throttled client gets `429` without any validation, even with a valid key.
2. **Verification slot**, as for passwords: a burst of parallel guesses from one client cannot overshoot its budget.
3. **Outcome.** Every failed validation counts (malformed, unknown, wrong secret, revoked, expired, owner disabled), once per identical token and client in the window (see [repeated identical attempts](#repeated-identical-attempts)). A success resets nothing: a key attempt has no pair counter, and a valid key never refills a client's budget.

Key and password failures from one client share its budget. Brute-forcing a key secret (256 bits) is not a realistic threat; the counters bound the database lookups and the log lines an anonymous client can cause.

## Answers

| Situation | Status | Headers | Body |
| --- | --- | --- | --- |
| Throttled | `429` | `Retry-After`: whole seconds until the longest applicable throttle ends, rounded up; no `WWW-Authenticate` | empty |
| Verification capacity exceeded | `503` | `Retry-After`: `BusyRetryAfter` (5 seconds); no `WWW-Authenticate` | empty |

The answer is produced by the scheme's challenge, so it only applies where authentication is required. An endpoint open to anonymous callers (the health check) ignores the refused credentials, as it ignores wrong ones.

The end-to-end tests record how the `dotnet` CLI reports and retries a `401`, `429` or `503` ([what the real clients do](e2e-tests.md#what-the-real-clients-do)): it retries six times within seconds without waiting for `Retry-After`. A restore with a wrong password is counted once and ends with `401`; the right password works right after. Distinct wrong passwords still lead to `429`. `nuget.exe` and Visual Studio have not been verified.

## Logging

Each trip is logged once, at warning level, as audit event 1801 `ThrottleTripped` (category `ElPaqueto.Security.Audit`, see [audit logging](audit-logging.md)), with the counter kind (`Client`, `Network`, `Pair`, `Account`, `Repeat`) and the client (its IPv4 address, IPv6 /64, or IPv6 /48 for the network counter), never the user name or anything from the credentials. Refused attempts are logged one by one at `Debug` only (event 1004 `AuthenticationThrottled`), so a flood does not flood the log with the shipped settings. Client addresses can be personal data: operators keep these logs under the same retention rules as their access logs.

## Limits

- **Single process.** Counters live in memory, per process, and are lost when the process restarts. V1 runs one node.
- **Shared addresses.** Clients behind one NAT, or behind a proxy that is not declared as trusted, share one client budget: one of them guessing wrong can throttle the others. Declare the proxy in `Security:ReverseProxy`.
- **Bounded memory.** Each counter kind, and the store of [failed-attempt fingerprints](#repeated-identical-attempts), holds at most `MaxTrackedEntries` entries (50000 by default). When a kind is full, forgotten counters are dropped, then a tenth of the kind, those closest to being forgotten; counters with an attempt in flight are never dropped. A counter holds at most its limit of timestamps, so the worst case is about `MaxTrackedEntries × (100 + 8 × limit)` bytes per kind; flooding with random user names creates counters of one failure each. An attacker with enough addresses to fill a kind can push older counters out early, but needs at least `MaxTrackedEntries / ClientFailureLimit` addresses (or /64 networks) per client window to do so, which already defeats per-client limits.
- **Account ceiling and many addresses.** An attacker with many addresses (many IPv4 addresses, or many IPv6 /48 networks) still gets one guess per address and per account throttle period. The password cost and the password policy ([password hashing](passwords.md)) remain the main defence against a large botnet.
