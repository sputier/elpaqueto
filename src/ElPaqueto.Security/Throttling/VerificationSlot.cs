namespace ElPaqueto.Security.Throttling;

/// <summary>
/// The right to run one credential verification, or the rejection to send instead.
/// </summary>
/// <remarks>
/// Report the outcome with <see cref="Succeeded"/> or <see cref="Failed"/>, then dispose. Disposing without
/// an outcome (the request was aborted, the verification threw) releases the slot without counting anything.
/// </remarks>
internal sealed class VerificationSlot : IDisposable
{
    private readonly Func<VerificationOutcome, bool>? _complete;
    private int _completed;

    private VerificationSlot(ThrottleRejection? rejection, Func<VerificationOutcome, bool>? complete, bool repeatsACountedFailure)
    {
        Rejection = rejection;
        _complete = complete;
        RepeatsACountedFailure = repeatsACountedFailure;
    }

    /// <summary>The answer to send instead of verifying, or <see langword="null"/> if the verification may run.</summary>
    public ThrottleRejection? Rejection { get; }

    /// <summary>
    /// The same credential attempt (client, user name, secret and credential state) already failed and was counted
    /// in the current window: verifying it again would give the same failure, and reporting it counts nothing.
    /// </summary>
    public bool RepeatsACountedFailure { get; }

    public static VerificationSlot Rejected(ThrottleRejection rejection) => new(rejection, null, false);

    public static VerificationSlot Granted(Func<VerificationOutcome, bool> complete, bool repeatsACountedFailure)
        => new(null, complete, repeatsACountedFailure);

    /// <summary>The credential was valid.</summary>
    public void Succeeded() => Complete(VerificationOutcome.Success);

    /// <summary>The credential was not valid (wrong password, unknown or disabled user, refused key).</summary>
    /// <returns>
    /// <see langword="true"/> if the failure was counted; <see langword="false"/> if the same attempt had already
    /// been counted in the window (or the slot was already completed).
    /// </returns>
    public bool Failed() => Complete(VerificationOutcome.Failure);

    public void Dispose() => Complete(VerificationOutcome.None);

    private bool Complete(VerificationOutcome outcome)
        => _complete is not null && Interlocked.Exchange(ref _completed, 1) == 0 && _complete(outcome);
}

internal enum VerificationOutcome
{
    None,
    Success,
    Failure,
}
