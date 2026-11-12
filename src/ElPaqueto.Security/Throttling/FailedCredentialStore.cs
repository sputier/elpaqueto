namespace ElPaqueto.Security.Throttling;

/// <summary>
/// The credential attempt a verification is about, so that a repeated identical failure is counted once.
/// </summary>
/// <remarks>
/// Not a record: its generated <c>ToString</c> would print the secret. Only <see cref="LoginThrottle"/> reads
/// the values, to compute a keyed fingerprint; they are never stored or logged.
/// </remarks>
internal readonly struct CredentialAttempt
{
    /// <param name="secret">The password or API key token exactly as sent.</param>
    /// <param name="state">
    /// What else decides the outcome of the verification: for a password, the stored hash of an active user, or
    /// one fixed value for every unknown or disabled user. A change of it (password reset, user enabled) makes
    /// the same secret a new attempt. Empty for an API key.
    /// </param>
    public CredentialAttempt(string secret, string state)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(state);

        Secret = secret;
        State = state;
    }

    public string Secret { get; }

    public string State { get; }
}

/// <summary>
/// The fingerprints of the failed credential attempts counted in the last window, so that an identical failure
/// is not counted again. Not thread-safe: <see cref="LoginThrottle"/> guards every access with its lock.
/// </summary>
/// <remarks>
/// Every fingerprint lives exactly one window after it was first recorded: repeating it does not extend it. The
/// store holds at most <c>capacity</c> fingerprints. When it is full, expired fingerprints are dropped, and if
/// none is, a new failure is <b>not</b> recorded: the caller then counts it, and counts its repetitions too
/// (fail closed). Nothing an attacker sends can therefore make a failure count less than once per attempt,
/// except repeating an attempt already counted.
/// </remarks>
internal sealed class FailedCredentialStore(TimeSpan window, int capacity)
{
    private readonly Dictionary<UInt128, long> _expiries = [];

    // Insertion order is expiry order: every fingerprint lives one window and the clock is monotonic.
    private readonly Queue<(UInt128 Fingerprint, long Expiry)> _order = new();
    private readonly long _windowTicks = window.Ticks;

    /// <summary>The fingerprints still remembered.</summary>
    public int Count(long now)
    {
        Purge(now);
        return _expiries.Count;
    }

    /// <summary>Whether the fingerprint was recorded less than one window ago.</summary>
    public bool Contains(UInt128 fingerprint, long now)
    {
        Purge(now);
        return _expiries.ContainsKey(fingerprint);
    }

    /// <summary>Records a counted failure; does nothing when the store is full.</summary>
    public void Add(UInt128 fingerprint, long now)
    {
        Purge(now);
        if (_expiries.Count >= capacity)
        {
            return;
        }

        var expiry = now + _windowTicks;
        if (_expiries.TryAdd(fingerprint, expiry))
        {
            _order.Enqueue((fingerprint, expiry));
        }
    }

    private void Purge(long now)
    {
        while (_order.TryPeek(out var oldest) && oldest.Expiry <= now)
        {
            _order.Dequeue();
            _expiries.Remove(oldest.Fingerprint);
        }
    }
}
