using System.ComponentModel.DataAnnotations;

namespace ElPaqueto.Security.Configuration;

/// <summary>
/// Limits on failed sign-in attempts and on concurrent password verifications (<c>Security:Throttling</c>).
/// </summary>
/// <remarks>
/// Failures are counted in memory, per process, in sliding windows: per client address (IPv6 grouped by
/// /64), per user name and client address, and per user name. Counters are lost when the process restarts.
/// </remarks>
public sealed class SecurityThrottlingOptions : IValidatableObject
{
    private const string Prefix = "Security:Throttling:";

    /// <summary>
    /// Failed attempts, for any user name, after which a client address is throttled.
    /// Setting: <c>Security:Throttling:ClientFailureLimit</c> (default 30).
    /// </summary>
    [Range(1, 1000, ErrorMessage = Prefix + "ClientFailureLimit must be between 1 and 1000.")]
    public int ClientFailureLimit { get; set; } = 30;

    /// <summary>
    /// The window in which <see cref="ClientFailureLimit"/> failures are counted.
    /// Setting: <c>Security:Throttling:ClientWindow</c> (default 10 minutes).
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true, ErrorMessage = Prefix + "ClientWindow must be between 00:00:01 and 1.00:00:00 (one day).")]
    public TimeSpan ClientWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Failed attempts for one user name from one client address after which that pair is throttled.
    /// Setting: <c>Security:Throttling:PairFailureLimit</c> (default 5).
    /// </summary>
    [Range(1, 1000, ErrorMessage = Prefix + "PairFailureLimit must be between 1 and 1000.")]
    public int PairFailureLimit { get; set; } = 5;

    /// <summary>
    /// The window in which <see cref="PairFailureLimit"/> failures are counted.
    /// Setting: <c>Security:Throttling:PairWindow</c> (default 15 minutes).
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true, ErrorMessage = Prefix + "PairWindow must be between 00:00:01 and 1.00:00:00 (one day).")]
    public TimeSpan PairWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Failed attempts for one user name, from all client addresses together, after which the user name
    /// is throttled for the clients that have failed for it recently.
    /// Setting: <c>Security:Throttling:AccountFailureLimit</c> (default 50).
    /// </summary>
    [Range(1, 1000, ErrorMessage = Prefix + "AccountFailureLimit must be between 1 and 1000.")]
    public int AccountFailureLimit { get; set; } = 50;

    /// <summary>
    /// The window in which <see cref="AccountFailureLimit"/> failures are counted.
    /// Setting: <c>Security:Throttling:AccountWindow</c> (default 1 hour).
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true, ErrorMessage = Prefix + "AccountWindow must be between 00:00:01 and 1.00:00:00 (one day).")]
    public TimeSpan AccountWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long the first trip of a counter throttles. Each further trip of the same counter, while it is
    /// still remembered, doubles the duration up to <see cref="MaximumThrottleDuration"/>.
    /// Setting: <c>Security:Throttling:InitialThrottleDuration</c> (default 1 minute).
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true, ErrorMessage = Prefix + "InitialThrottleDuration must be between 00:00:01 and 1.00:00:00 (one day).")]
    public TimeSpan InitialThrottleDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The longest throttle duration. Setting: <c>Security:Throttling:MaximumThrottleDuration</c>
    /// (default 15 minutes). There is no permanent lockout.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true, ErrorMessage = Prefix + "MaximumThrottleDuration must be between 00:00:01 and 1.00:00:00 (one day).")]
    public TimeSpan MaximumThrottleDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The largest number of counters kept for each kind (client, pair, user name). When a kind is full,
    /// expired counters are dropped first, then those closest to expiry.
    /// Setting: <c>Security:Throttling:MaxTrackedEntries</c> (default 50000).
    /// </summary>
    [Range(100, 10_000_000, ErrorMessage = Prefix + "MaxTrackedEntries must be between 100 and 10000000.")]
    public int MaxTrackedEntries { get; set; } = 50_000;

    /// <summary>
    /// The number of password verifications that may run at the same time.
    /// Setting: <c>Security:Throttling:MaxConcurrentVerifications</c> (default 0: the processor count).
    /// </summary>
    [Range(0, 1024, ErrorMessage = Prefix + "MaxConcurrentVerifications must be between 0 (the processor count) and 1024.")]
    public int MaxConcurrentVerifications { get; set; }

    /// <summary>
    /// The number of verifications that may wait for a free slot; beyond it the request gets 503.
    /// Also bounds the attempts waiting for another attempt of the same client to finish.
    /// Setting: <c>Security:Throttling:VerificationQueueLimit</c> (default 100).
    /// </summary>
    [Range(0, 10_000, ErrorMessage = Prefix + "VerificationQueueLimit must be between 0 and 10000.")]
    public int VerificationQueueLimit { get; set; } = 100;

    /// <summary>
    /// The <c>Retry-After</c> value sent with a 503 when verification capacity is exceeded.
    /// Setting: <c>Security:Throttling:BusyRetryAfter</c> (default 5 seconds).
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true, ErrorMessage = Prefix + "BusyRetryAfter must be between 00:00:01 and 00:10:00.")]
    public TimeSpan BusyRetryAfter { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The effective number of concurrent verifications.</summary>
    internal int EffectiveMaxConcurrentVerifications
        => MaxConcurrentVerifications == 0 ? Environment.ProcessorCount : MaxConcurrentVerifications;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (InitialThrottleDuration > MaximumThrottleDuration)
        {
            yield return new ValidationResult(Prefix + "InitialThrottleDuration must not be greater than " + Prefix + "MaximumThrottleDuration.");
        }
    }
}
