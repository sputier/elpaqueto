using System.Net;
using System.Threading.RateLimiting;
using ElPaqueto.Security.Auditing;
using ElPaqueto.Security.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Throttling;

/// <summary>
/// In-memory, per-process implementation of <see cref="ILoginThrottle"/>.
/// </summary>
/// <remarks>
/// <para>Three kinds of failure counters, each a sliding window of failure times:</para>
/// <list type="bullet">
/// <item><b>Client</b> (IPv4 address or IPv6 /64): too many failures throttle every attempt from that client.</item>
/// <item><b>Network</b> (IPv6 /48, the usual size of one site's allocation): a higher limit, so that rotating
/// through the /64 networks of one allocation does not multiply the client budget.</item>
/// <item><b>Pair</b> (user name, client): too many failures throttle that user name from that client.</item>
/// <item><b>Account</b> (user name, known or not): too many failures from all clients together throttle that
/// user name only for the clients that have failed for it recently. A client that has not failed for the user
/// name, such as its legitimate owner, is never refused by this counter.</item>
/// </list>
/// <para>A counter that reaches its limit trips: it throttles for a duration that doubles with each trip while
/// the counter is remembered, up to a maximum, and starts counting again from zero. A counter is forgotten one
/// window after its last failure or the end of its last throttle, whichever is later: there is no permanent
/// lockout.</para>
/// <para>Attempts for the same pair run one at a time, and a client cannot have more verifications in flight
/// than its remaining failure budget: a burst of parallel guesses from one client therefore cannot overshoot
/// the client or pair limits. The account limit can be overshot by attempts already in flight from other
/// clients, which the global verification capacity bounds.
/// Waiting attempts are bounded; beyond the bound, and beyond the global verification capacity, the answer
/// is 503.</para>
/// <para>Every counter kind holds at most <see cref="SecurityThrottlingOptions.MaxTrackedEntries"/> entries, so
/// memory stays bounded whatever the number of user names or addresses an attacker sends.</para>
/// </remarks>
internal sealed class LoginThrottle : ILoginThrottle, IDisposable
{
    private readonly Lock _gate = new();
    private readonly SecurityThrottlingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<Audit> _logger;
    private readonly ConcurrencyLimiter _capacity;
    private readonly CounterStore<ClientKey> _clients;
    private readonly CounterStore<NetworkKey> _networks;
    private readonly CounterStore<PairKey> _pairs;
    private readonly CounterStore<AccountKey> _accounts;
    private readonly long _origin;
    private int _waiting;

    public LoginThrottle(IOptions<ElPaquetoSecurityOptions> options, TimeProvider time, ILogger<Audit> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value.Throttling;
        _time = time;
        _logger = logger;
        _capacity = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = _options.EffectiveMaxConcurrentVerifications,
            QueueLimit = _options.VerificationQueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
        _clients = new CounterStore<ClientKey>(_options.ClientWindow, _options.ClientFailureLimit, _options.MaxTrackedEntries);
        _networks = new CounterStore<NetworkKey>(_options.ClientWindow, _options.NetworkFailureLimit, _options.MaxTrackedEntries);
        _pairs = new CounterStore<PairKey>(_options.PairWindow, _options.PairFailureLimit, _options.MaxTrackedEntries);
        _accounts = new CounterStore<AccountKey>(_options.AccountWindow, _options.AccountFailureLimit, _options.MaxTrackedEntries);
        _origin = MonotonicTicks();
    }

    public ThrottleRejection? Check(IPAddress? client, string? userName)
    {
        var keys = Keys.For(client, userName);

        lock (_gate)
        {
            return Throttled(keys, Now());
        }
    }

    public void RecordSuccess(IPAddress? client, string? userName)
    {
        var keys = Keys.For(client, userName);
        if (keys.Pair is not { } pairKey)
        {
            return;
        }

        lock (_gate)
        {
            var now = Now();
            if (_pairs.Find(pairKey, now) is { } pair)
            {
                pair.Reset();
                _pairs.RemoveIfIdle(pairKey, pair, now);
            }
        }
    }

    public async ValueTask<VerificationSlot> EnterVerificationAsync(IPAddress? client, string? userName, CancellationToken cancellationToken)
    {
        var keys = Keys.For(client, userName);

        // An attempt that waits keeps its place in the queue until it leaves, granted or not.
        var place = new QueuePlace();
        try
        {
            return await EnterAsync(keys, place, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (place.Held)
            {
                lock (_gate)
                {
                    _waiting--;
                }
            }
        }
    }

    private async ValueTask<VerificationSlot> EnterAsync(Keys keys, QueuePlace place, CancellationToken cancellationToken)
    {
        while (true)
        {
            Counter? busy;
            Task? released = null;
            Counter clientCounter;
            Counter? pairCounter = null;

            lock (_gate)
            {
                var now = Now();
                if (Throttled(keys, now) is { } rejection)
                {
                    return VerificationSlot.Rejected(rejection);
                }

                clientCounter = _clients.GetOrAdd(keys.Client, now);
                if (keys.Pair is { } pairKey)
                {
                    pairCounter = _pairs.GetOrAdd(pairKey, now);
                }

                // One verification at a time per pair; a client never has more in flight than its remaining budget.
                busy = pairCounter is { InFlight: > 0 } ? pairCounter
                    : clientCounter.InFlight > 0 && clientCounter.FailureCount + clientCounter.InFlight >= _options.ClientFailureLimit ? clientCounter
                    : null;

                if (busy is null)
                {
                    clientCounter.InFlight++;
                    if (pairCounter is not null)
                    {
                        pairCounter.InFlight++;
                    }
                }
                else
                {
                    if (!place.Held)
                    {
                        if (_waiting >= _options.VerificationQueueLimit)
                        {
                            return VerificationSlot.Rejected(Busy());
                        }

                        _waiting++;
                        place.Held = true;
                    }

                    busy.Waiters++;
                    busy.Released ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    released = busy.Released.Task;
                }
            }

            if (busy is not null)
            {
                try
                {
                    await released!.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        busy.Waiters--;
                    }
                }

                // Another attempt finished: it may have tripped a counter, or succeeded (the caller then finds
                // the credential in its cache). Decide again.
                continue;
            }

            RateLimitLease lease;
            try
            {
                lease = await _capacity.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Complete(keys, clientCounter, pairCounter, lease: null, VerificationOutcome.None);
                throw;
            }

            if (!lease.IsAcquired)
            {
                Complete(keys, clientCounter, pairCounter, lease, VerificationOutcome.None);
                return VerificationSlot.Rejected(Busy());
            }

            return VerificationSlot.Granted(outcome => Complete(keys, clientCounter, pairCounter, lease, outcome));
        }
    }

    public void Dispose() => _capacity.Dispose();

    /// <summary>The number of counters of a kind currently held. For tests and diagnostics.</summary>
    internal int TrackedEntries(ThrottleScope scope)
    {
        lock (_gate)
        {
            return scope switch
            {
                ThrottleScope.Client => _clients.Count,
                ThrottleScope.Network => _networks.Count,
                ThrottleScope.Pair => _pairs.Count,
                _ => _accounts.Count,
            };
        }
    }

    /// <summary>Reserved verifications and waiting attempts across all counters. For tests and diagnostics.</summary>
    internal int OutstandingReservations()
    {
        lock (_gate)
        {
            return _clients.Outstanding + _pairs.Outstanding + _waiting;
        }
    }

    /// <summary>The failures currently counted in a window. For tests and diagnostics.</summary>
    internal int FailureCount(ThrottleScope scope, IPAddress? client, string? userName)
    {
        var keys = Keys.For(client, userName);

        lock (_gate)
        {
            var now = Now();
            var counter = scope switch
            {
                ThrottleScope.Client => _clients.Find(keys.Client, now),
                ThrottleScope.Network => keys.Network is { } network ? _networks.Find(network, now) : null,
                ThrottleScope.Pair => keys.Pair is { } pair ? _pairs.Find(pair, now) : null,
                _ => keys.Account is { } account ? _accounts.Find(account, now) : null,
            };
            return counter?.FailureCount ?? 0;
        }
    }

    private void Complete(Keys keys, Counter clientCounter, Counter? pairCounter, RateLimitLease? lease, VerificationOutcome outcome)
    {
        List<(ThrottleScope Scope, TimeSpan Duration)>? trips = null;

        lock (_gate)
        {
            var now = Now();
            clientCounter.InFlight--;
            if (pairCounter is not null)
            {
                pairCounter.InFlight--;
            }

            switch (outcome)
            {
                case VerificationOutcome.Failure:
                    AddTrip(ref trips, ThrottleScope.Client, _clients.RecordFailure(clientCounter, now, _options));
                    if (keys.Network is { } networkKey)
                    {
                        var network = _networks.GetOrAdd(networkKey, now);
                        AddTrip(ref trips, ThrottleScope.Network, _networks.RecordFailure(network, now, _options));
                    }
                    if (pairCounter is not null)
                    {
                        AddTrip(ref trips, ThrottleScope.Pair, _pairs.RecordFailure(pairCounter, now, _options));
                    }

                    if (keys.Account is { } accountKey)
                    {
                        var account = _accounts.GetOrAdd(accountKey, now);
                        AddTrip(ref trips, ThrottleScope.Account, _accounts.RecordFailure(account, now, _options));
                    }

                    break;

                case VerificationOutcome.Success:
                    // Only the pair is reset: a valid account must not refill a client's budget for other user
                    // names, and the owner logging in must not refill a distributed attacker's budget for its account.
                    pairCounter?.Reset();
                    break;
            }

            clientCounter.Signal();
            pairCounter?.Signal();
            _clients.RemoveIfIdle(keys.Client, clientCounter, now);
            if (keys.Pair is { } pairKey && pairCounter is not null)
            {
                _pairs.RemoveIfIdle(pairKey, pairCounter, now);
            }
        }

        lease?.Dispose();

        if (trips is not null)
        {
            var client = ClientKey.Describe(keys.Address);
            var network = NetworkKey.Describe(keys.Address);
            foreach (var (scope, duration) in trips)
            {
                AuditLog.ThrottleTripped(_logger, scope, scope == ThrottleScope.Network ? network : client, (long)duration.TotalSeconds);
            }
        }
    }

    private static void AddTrip(ref List<(ThrottleScope, TimeSpan)>? trips, ThrottleScope scope, TimeSpan? duration)
    {
        if (duration is { } value)
        {
            (trips ??= []).Add((scope, value));
        }
    }

    private ThrottleRejection? Throttled(Keys keys, long now)
    {
        var until = 0L;

        if (_clients.Find(keys.Client, now) is { } client && client.IsThrottled(now))
        {
            until = Math.Max(until, client.ThrottledUntil);
        }

        if (keys.Network is { } networkKey && _networks.Find(networkKey, now) is { } network && network.IsThrottled(now))
        {
            until = Math.Max(until, network.ThrottledUntil);
        }

        if (keys.Pair is { } pairKey)
        {
            var pair = _pairs.Find(pairKey, now);
            if (pair is not null && pair.IsThrottled(now))
            {
                until = Math.Max(until, pair.ThrottledUntil);
            }

            // The account counter only refuses clients that have failed for this user name recently.
            if (pair is not null
                && pair.HasFailedSince(now - _options.PairWindow.Ticks)
                && keys.Account is { } accountKey
                && _accounts.Find(accountKey, now) is { } account
                && account.IsThrottled(now))
            {
                until = Math.Max(until, account.ThrottledUntil);
            }
        }

        return until > now
            ? new ThrottleRejection(StatusCodes.Status429TooManyRequests, TimeSpan.FromTicks(until - now))
            : null;
    }

    private ThrottleRejection Busy() => new(StatusCodes.Status503ServiceUnavailable, _options.BusyRetryAfter);

    // Monotonic: a change of the wall clock neither extends nor ends a throttle. The offset keeps every value far
    // from zero, which counters use as "never".
    private long Now() => MonotonicTicks() - _origin + TimeSpan.TicksPerDay * 3;

    private long MonotonicTicks()
        => (long)((Int128)_time.GetTimestamp() * TimeSpan.TicksPerSecond / _time.TimestampFrequency);

    private sealed class QueuePlace
    {
        public bool Held { get; set; }
    }

    private readonly record struct Keys(IPAddress? Address, ClientKey Client, NetworkKey? Network, AccountKey? Account, PairKey? Pair)
    {
        public static Keys For(IPAddress? address, string? userName)
        {
            var client = ClientKey.From(address);
            var network = NetworkKey.From(address);
            if (userName is null)
            {
                return new Keys(address, client, network, null, null);
            }

            var account = AccountKey.From(userName);
            return new Keys(address, client, network, account, new PairKey(client, account));
        }
    }
}

/// <summary>The kinds of failure counters.</summary>
internal enum ThrottleScope
{
    /// <summary>A client address (IPv6 by /64).</summary>
    Client,

    /// <summary>An IPv6 /48 network.</summary>
    Network,

    /// <summary>A user name tried from a client address.</summary>
    Pair,

    /// <summary>A user name, from all clients.</summary>
    Account,
}
