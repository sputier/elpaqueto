using ElPaqueto.Security.Configuration;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.ApiKeys;

/// <summary>
/// Decides when the last-use timestamp of a key is written, so that read traffic does not become
/// write traffic. Process-wide (singleton).
/// </summary>
/// <remarks>
/// Two gates, both of the width <c>Security:ApiKeys:LastUsedUpdateInterval</c>:
/// <list type="number">
/// <item>the stored timestamp, read with the key anyway: no write while it is recent enough. This holds
/// across processes and restarts;</item>
/// <item>an in-memory claim per key: among concurrent requests that all read the same stale timestamp,
/// only the first one writes.</item>
/// </list>
/// The write itself is conditional on the stored value, so two processes racing still produce at most
/// one effective update per interval.
/// </remarks>
internal sealed class ApiKeyUsageTracker(IOptions<ElPaquetoSecurityOptions> options)
{
    // Bound on the in-memory claims: beyond it, expired claims are dropped.
    private const int PruneThreshold = 1024;

    private readonly TimeSpan _interval = options.Value.ApiKeys.LastUsedUpdateInterval;
    private readonly Dictionary<Guid, DateTime> _claims = [];
    private readonly Lock _lock = new();

    public TimeSpan Interval => _interval;

    /// <summary>
    /// Returns whether the caller should write the last-use timestamp now, and if so claims the write
    /// for this interval.
    /// </summary>
    /// <param name="apiKeyId">The key.</param>
    /// <param name="storedLastUsedAt">The timestamp read from the database with the key.</param>
    /// <param name="now">The current UTC time.</param>
    /// <returns><see langword="true"/> when the caller must write.</returns>
    public bool TryClaimWrite(Guid apiKeyId, DateTime? storedLastUsedAt, DateTime now)
    {
        if (storedLastUsedAt is { } stored && now - stored < _interval)
        {
            return false;
        }

        lock (_lock)
        {
            if (_claims.TryGetValue(apiKeyId, out var claimed) && now - claimed < _interval)
            {
                return false;
            }

            if (_claims.Count >= PruneThreshold)
            {
                Prune(now);
            }

            _claims[apiKeyId] = now;
            return true;
        }
    }

    /// <summary>The oldest timestamp that no longer suppresses a write at <paramref name="now"/>.</summary>
    /// <param name="now">The current UTC time.</param>
    /// <returns>The threshold for the conditional update.</returns>
    public DateTime StaleBefore(DateTime now) => now - _interval;

    private void Prune(DateTime now)
    {
        foreach (var (key, claimed) in _claims.ToList())
        {
            if (now - claimed >= _interval)
            {
                _claims.Remove(key);
            }
        }
    }
}
