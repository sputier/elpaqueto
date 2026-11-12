# Sign-in throttling

Failed sign-in attempts and concurrent password verifications are limited by `LoginThrottle` (`src/ElPaqueto.Security/Throttling/`), an in-memory service that the authentication handler consults **before** it looks the user up or verifies anything. Settings: [`Security:Throttling`](../user/configuration.md#securitythrottling).

## Threat model

The server is meant to be reachable from the Internet behind a TLS-terminating proxy, with no other infrastructure. The throttle addresses:

- **Online password guessing** against one account, from one client or from many.
- **Password spraying**: a few common passwords against many user names from one client.
- **User enumeration**: learning which user names exist from the answers. The throttle never knows whether a user exists: unknown, disabled and existing user names are counted identically, so its decisions cannot reveal it. Its answers (429, 503) are the only ones that differ from the uniform `401`, and they depend only on the attempts, never on the account.
- **CPU exhaustion** with deliberately failing verifications: each verification costs a PBKDF2 hash ([password hashing](passwords.md)). Throttled attempts cost no hash at all, and the number of hashes running at once is bounded.
- **Lockout of a legitimate user by an attacker** (denial of service). This is the constraint that shapes the design: a counter that refuses the account's owner whatever its address would let anyone lock out anyone, including CI accounts, by sending wrong passwords.

Not addressed: distributed counters, CAPTCHA, permanent lockout requiring an administrator, IP allow or deny lists. Operators who need more can add limits at the proxy.

## Counters

| Counter | Key | Default | Refuses |
| --- | --- | --- | --- |
| Client | client address: the IPv4 address, or the IPv6 /64 network | 30 failures in 10 minutes | every attempt from that client |
| Network | IPv6 /48 network (IPv4 clients have none) | 300 failures in 10 minutes | every attempt from that /48 |
| Pair | (user name, client) | 5 failures in 15 minutes | that user name from that client |
| Account | user name, known or not | 50 failures in 1 hour | that user name, **only from clients with a failure for it in the last pair window** |

- **Why the account counter only refuses clients that failed.** A distributed attacker cannot be stopped per client, so a per-account ceiling is needed; but a ceiling that refused everyone would hand the attacker a lockout. Once the ceiling trips, every client that has guessed wrong for that user name recently is refused, and each new attacking address gets one guess before it is refused too. The owner, signing in from an address with no recent failure for the account, is never refused by this counter. If the owner did mistype from that address, it is refused until its own pair failure is older than the pair window (15 minutes by default) or until the account throttle ends, whichever is first.
- **IPv6 by /64, and by /48.** One subscriber usually gets a whole /64; counting single IPv6 addresses would let one client rotate through billions of them. A site, a rented server or a tunnel broker account often gets a /48, which holds 65536 /64 networks: the network counter keeps rotating through them from multiplying the budget. Its limit is higher than the client limit, because one /48 can hold many legitimate clients (an office, a campus). IPv4 addresses seen through a dual-stack socket (`::ffff:a.b.c.d`) are unmapped and counted as IPv4, never grouped. A request without an address (a Unix socket) is counted in one shared bucket.
- **User names** are normalized like the stored ones (upper-case invariant) and kept as a 128-bit prefix of their SHA-256: a long or random user name costs the same few bytes as any other, and no user name is kept in clear in memory or written to the logs.

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
5. Reports the outcome. A failure (wrong password, unknown user, disabled user) counts on every counter that applies. A success resets **only the pair**: a valid account must not refill a client's budget for other user names, and the owner signing in must not refill a distributed attacker's budget against its account. An aborted request counts nothing.

Malformed `Authorization` headers are refused by the parser before the throttle and are not counted: they cost no hash.

## Answers

| Situation | Status | Headers | Body |
| --- | --- | --- | --- |
| Throttled | `429` | `Retry-After`: whole seconds until the longest applicable throttle ends, rounded up; no `WWW-Authenticate` | empty |
| Verification capacity exceeded | `503` | `Retry-After`: `BusyRetryAfter` (5 seconds); no `WWW-Authenticate` | empty |

The answer is produced by the scheme's challenge, so it only applies where authentication is required. An endpoint open to anonymous callers (the health check) ignores the refused credentials, as it ignores wrong ones.

How the NuGet clients (`dotnet restore`, `dotnet nuget push`, Visual Studio) report and retry a `429` or a `503` has not been verified yet: the end-to-end client tests record it.

## Logging

Each trip is logged once, at warning level, with event id 1801, the counter kind (`Client`, `Network`, `Pair`, `Account`) and the client (its IPv4 address, IPv6 /64, or IPv6 /48 for the network counter), never the user name or anything from the credentials. Refused attempts are not logged one by one, so a flood does not flood the log. Client addresses can be personal data: operators keep these logs under the same retention rules as their access logs.

## Limits

- **Single process.** Counters live in memory, per process, and are lost when the process restarts. V1 runs one node.
- **Shared addresses.** Clients behind one NAT, or behind a proxy that is not declared as trusted, share one client budget: one of them guessing wrong can throttle the others. Declare the proxy in `Security:ReverseProxy`.
- **Bounded memory.** Each counter kind holds at most `MaxTrackedEntries` counters (50000 by default). When a kind is full, forgotten counters are dropped, then a tenth of the kind, those closest to being forgotten; counters with an attempt in flight are never dropped. A counter holds at most its limit of timestamps, so the worst case is about `MaxTrackedEntries × (100 + 8 × limit)` bytes per kind; flooding with random user names creates counters of one failure each. An attacker with enough addresses to fill a kind can push older counters out early, but needs at least `MaxTrackedEntries / ClientFailureLimit` addresses (or /64 networks) per client window to do so, which already defeats per-client limits.
- **Account ceiling and many addresses.** An attacker with many addresses (many IPv4 addresses, or many IPv6 /48 networks) still gets one guess per address and per account throttle period. The password cost and the password policy ([password hashing](passwords.md)) remain the main defence against a large botnet.
