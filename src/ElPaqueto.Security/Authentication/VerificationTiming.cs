namespace ElPaqueto.Security.Authentication;

/// <summary>
/// The typical duration of a password verification, measured on this server.
/// </summary>
/// <remarks>
/// A repeated identical failure is answered without hashing (see docs/technical/throttling.md). It then waits for
/// this duration, without using a processor, so that it takes about as long as a verification: the response time
/// is not a cheap signal of whether an attempt was verified or recognised as a repetition. It is an average, so
/// it does not reproduce the jitter of a real verification.
/// </remarks>
internal sealed class VerificationTiming
{
    /// <summary>A sample is counted as at most this long: a cold start or a pause cannot make repetitions wait longer.</summary>
    internal static readonly TimeSpan MaximumSample = TimeSpan.FromSeconds(2);

    // Exponential moving average, weight 1/8 for each new measurement.
    private const int Weight = 8;

    private long _typicalTicks;

    /// <summary>The moving average of the measured verifications; zero before the first one.</summary>
    public TimeSpan Typical => TimeSpan.FromTicks(Volatile.Read(ref _typicalTicks));

    /// <summary>Adds the duration of one verification.</summary>
    public void Record(TimeSpan elapsed)
    {
        var sample = Math.Clamp(elapsed.Ticks, 0, MaximumSample.Ticks);
        long current, next;
        do
        {
            current = Volatile.Read(ref _typicalTicks);
            next = current == 0 ? sample : current + ((sample - current) / Weight);
        }
        while (Interlocked.CompareExchange(ref _typicalTicks, next, current) != current);
    }
}
