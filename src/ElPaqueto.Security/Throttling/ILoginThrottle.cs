using System.Net;

namespace ElPaqueto.Security.Throttling;

/// <summary>
/// Limits failed sign-in attempts and concurrent password verifications.
/// </summary>
/// <remarks>
/// <para>A caller that checks a credential:</para>
/// <list type="number">
/// <item>calls <see cref="Check"/> before any lookup or verification, and refuses the request if it
/// returns a rejection;</item>
/// <item>when a cheap, already-trusted result is available (the verification cache), calls
/// <see cref="RecordSuccess"/> on success, and nothing on a miss;</item>
/// <item>otherwise calls <see cref="EnterVerificationAsync"/>, refuses the request if the slot carries a
/// rejection, and else verifies the credential and reports the outcome on the slot.</item>
/// </list>
/// <para>The user name is the one presented, known or not. It is <see langword="null"/> for a credential
/// without one (an API key header), which is then only counted against the client.</para>
/// </remarks>
internal interface ILoginThrottle
{
    /// <summary>Returns a rejection if the client or the user name is currently throttled.</summary>
    public ThrottleRejection? Check(IPAddress? client, string? userName);

    /// <summary>Records a success that did not need a verification slot (a verification cache hit).</summary>
    public void RecordSuccess(IPAddress? client, string? userName);

    /// <summary>
    /// Waits until this attempt may run a verification and reserves it, or returns a slot that carries
    /// the rejection to send.
    /// </summary>
    public ValueTask<VerificationSlot> EnterVerificationAsync(IPAddress? client, string? userName, CancellationToken cancellationToken);
}

/// <summary>The answer to send instead of verifying a credential.</summary>
/// <param name="StatusCode">429 when throttled, 503 when verification capacity is exceeded.</param>
/// <param name="RetryAfter">How long the client should wait.</param>
internal readonly record struct ThrottleRejection(int StatusCode, TimeSpan RetryAfter)
{
    /// <summary>The <c>Retry-After</c> header value: whole seconds, rounded up, at least 1.</summary>
    public string RetryAfterHeaderValue
        => Math.Max(1L, (long)Math.Ceiling(RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
