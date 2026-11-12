namespace ElPaqueto.Security.Passwords;

/// <summary>
/// The outcome of verifying a password.
/// </summary>
public enum PasswordVerification
{
    /// <summary>The password is wrong, or the user cannot authenticate.</summary>
    Failed = 0,

    /// <summary>The password is correct.</summary>
    Success = 1,

    /// <summary>
    /// The password is correct but its hash uses a lower cost or an older format: the caller should
    /// hash the password again and store the new hash.
    /// </summary>
    SuccessRehashNeeded = 2,
}
