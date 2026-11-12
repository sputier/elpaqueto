using System.Diagnostics;

namespace ElPaqueto.Security.Authentication;

/// <summary>
/// The typical duration of a password verification, measured on this server.
/// </summary>
/// <remarks>
/// A repeated identical failure is answered without hashing (see docs/technical/throttling.md). It then waits for
/// this duration, without using a processor, so that it takes as long as a verification: whether an attempt was
/// verified or recognised as a repetition cannot be told from the response time, and so neither can a change of
/// the account's state between two identical attempts.
/// </remarks>
internal sealed class VerificationTiming
{
    // Exponential moving average, weight 1/8 for each new measurement.
    private const int Weight = 8;

    private long _typicalTicks;

    /// <summary>The moving average of the measured verifications; zero before the first one.</summary>
    public TimeSpan Typical => TimeSpan.FromTicks(Volatile.Read(ref _typicalTicks));

    /// <summary>Measures a verification that started at <paramref name="startTimestamp"/> (<see cref="Stopwatch.GetTimestamp"/>).</summary>
    public void Record(long startTimestamp)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp).Ticks;
        long current, next;
        do
        {
            current = Volatile.Read(ref _typicalTicks);
            next = current == 0 ? elapsed : current + ((elapsed - current) / Weight);
        }
        while (Interlocked.CompareExchange(ref _typicalTicks, next, current) != current);
    }
}
