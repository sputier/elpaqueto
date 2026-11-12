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
    private readonly Action<VerificationOutcome>? _complete;
    private int _completed;

    private VerificationSlot(ThrottleRejection? rejection, Action<VerificationOutcome>? complete)
    {
        Rejection = rejection;
        _complete = complete;
    }

    /// <summary>The answer to send instead of verifying, or <see langword="null"/> if the verification may run.</summary>
    public ThrottleRejection? Rejection { get; }

    public static VerificationSlot Rejected(ThrottleRejection rejection) => new(rejection, null);

    public static VerificationSlot Granted(Action<VerificationOutcome> complete) => new(null, complete);

    /// <summary>The credential was valid.</summary>
    public void Succeeded() => Complete(VerificationOutcome.Success);

    /// <summary>The credential was not valid (wrong password, unknown or disabled user).</summary>
    public void Failed() => Complete(VerificationOutcome.Failure);

    public void Dispose() => Complete(VerificationOutcome.None);

    private void Complete(VerificationOutcome outcome)
    {
        if (_complete is not null && Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _complete(outcome);
        }
    }
}

internal enum VerificationOutcome
{
    None,
    Success,
    Failure,
}
