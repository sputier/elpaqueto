using ElPaqueto.Security.Configuration;

namespace ElPaqueto.Security.Throttling;

/// <summary>
/// The failure counter of one key. Not thread-safe: <see cref="LoginThrottle"/> guards every access with its lock.
/// </summary>
internal sealed class Counter
{
    private const long Never = 0;

    // Failure times (UTC ticks) inside the window, oldest first. Never longer than the limit: it is cleared on a trip.
    private readonly Queue<long> _failures = new();

    public long ThrottledUntil { get; private set; }

    public long LastFailure { get; private set; } = Never;

    public int Trips { get; private set; }

    public int InFlight { get; set; }

    public int Waiters { get; set; }

    public TaskCompletionSource? Released { get; set; }

    public int FailureCount => _failures.Count;

    public bool IsThrottled(long now) => now < ThrottledUntil;

    public bool HasFailedSince(long since) => LastFailure != Never && LastFailure > since;

    public bool IsBusy => InFlight > 0 || Waiters > 0;

    /// <summary>The time after which this counter has nothing left to remember.</summary>
    public long ForgetAfter(long windowTicks)
    {
        var anchor = Math.Max(ThrottledUntil, LastFailure);
        return anchor == Never ? Never : anchor + windowTicks;
    }

    public void Prune(long now, long windowTicks)
    {
        while (_failures.Count > 0 && _failures.Peek() <= now - windowTicks)
        {
            _failures.Dequeue();
        }
    }

    /// <summary>Counts a failure; returns the throttle duration if it trips the counter.</summary>
    public TimeSpan? AddFailure(long now, int limit, TimeSpan initial, TimeSpan maximum)
    {
        _failures.Enqueue(now);
        LastFailure = now;

        if (_failures.Count < limit)
        {
            return null;
        }

        // 1, 2, 4... times the initial duration, capped. The shift is bounded so it cannot overflow.
        var factor = 1L << Math.Min(Trips, 30);
        var ticks = initial.Ticks > maximum.Ticks / factor ? maximum.Ticks : Math.Min(initial.Ticks * factor, maximum.Ticks);
        Trips++;
        ThrottledUntil = now + ticks;
        _failures.Clear();
        return TimeSpan.FromTicks(ticks);
    }

    /// <summary>Forgets every failure and trip (a successful sign-in for a pair).</summary>
    public void Reset()
    {
        _failures.Clear();
        LastFailure = Never;
        ThrottledUntil = Never;
        Trips = 0;
    }

    /// <summary>Wakes the attempts waiting for an attempt on this counter to finish.</summary>
    public void Signal()
    {
        var released = Released;
        Released = null;
        released?.TrySetResult();
    }
}

/// <summary>
/// A bounded map of counters of one kind. Not thread-safe: <see cref="LoginThrottle"/> guards every access.
/// </summary>
/// <remarks>
/// Expired counters are removed when they are looked up. When the map is full, expired counters are
/// dropped, then a tenth of the map, those closest to being forgotten; counters with an attempt in
/// flight or waiting are never dropped (their number is bounded by the verification capacity).
/// </remarks>
internal sealed class CounterStore<TKey>(TimeSpan window, int limit, int capacity)
    where TKey : notnull
{
    private readonly Dictionary<TKey, Counter> _counters = [];
    private readonly long _windowTicks = window.Ticks;

    public int Count => _counters.Count;

    public Counter? Find(TKey key, long now)
    {
        if (!_counters.TryGetValue(key, out var counter))
        {
            return null;
        }

        counter.Prune(now, _windowTicks);
        if (IsForgotten(counter, now))
        {
            _counters.Remove(key);
            return null;
        }

        return counter;
    }

    public Counter GetOrAdd(TKey key, long now)
    {
        if (Find(key, now) is { } existing)
        {
            return existing;
        }

        if (_counters.Count >= capacity)
        {
            MakeRoom(now);
        }

        var counter = new Counter();
        _counters.Add(key, counter);
        return counter;
    }

    public TimeSpan? RecordFailure(Counter counter, long now, SecurityThrottlingOptions options)
    {
        counter.Prune(now, _windowTicks);
        return counter.AddFailure(now, limit, options.InitialThrottleDuration, options.MaximumThrottleDuration);
    }

    public void RemoveIfIdle(TKey key, Counter counter, long now)
    {
        if (IsForgotten(counter, now) && _counters.TryGetValue(key, out var current) && ReferenceEquals(current, counter))
        {
            _counters.Remove(key);
        }
    }

    private bool IsForgotten(Counter counter, long now)
        => !counter.IsBusy && counter.FailureCount == 0 && now >= counter.ForgetAfter(_windowTicks);

    private void MakeRoom(long now)
    {
        foreach (var (key, counter) in _counters)
        {
            counter.Prune(now, _windowTicks);
            if (IsForgotten(counter, now))
            {
                _counters.Remove(key);
            }
        }

        if (_counters.Count < capacity)
        {
            return;
        }

        var evictable = _counters
            .Where(entry => !entry.Value.IsBusy)
            .OrderBy(entry => entry.Value.ForgetAfter(_windowTicks))
            .Take(Math.Max(1, capacity / 10))
            .Select(entry => entry.Key)
            .ToList();

        foreach (var key in evictable)
        {
            _counters.Remove(key);
        }
    }
}
