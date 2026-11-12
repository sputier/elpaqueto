using ElPaqueto.Security.Data;

namespace ElPaqueto.Security.Passwords;

/// <summary>
/// Hashes, verifies and validates passwords. Passwords are never logged and never stored.
/// </summary>
public interface IPasswordService
{
    /// <summary>
    /// Hashes a password with the configured cost and a random salt.
    /// </summary>
    /// <param name="password">The password, as entered.</param>
    /// <returns>The hash to store.</returns>
    /// <remarks>Call <see cref="Validate"/> first: this method does not apply the policy.</remarks>
    public string Hash(string password);

    /// <summary>
    /// Verifies a password. The work done is the same whether the user exists, is disabled, has a
    /// valid hash or not, so response times do not reveal which user names exist.
    /// </summary>
    /// <param name="user">The user, or <see langword="null"/> when the user name is unknown.</param>
    /// <param name="password">The submitted password.</param>
    /// <returns>The outcome of the verification.</returns>
    public PasswordVerification Verify(User? user, string password);

    /// <summary>
    /// Checks a new password against the policy. Applied when a password is set, never at login.
    /// </summary>
    /// <param name="password">The candidate password.</param>
    /// <param name="userName">The user name, to reject a password equal to it.</param>
    /// <returns>The policy violations, empty when the password is acceptable. Messages never contain the password.</returns>
    public IReadOnlyList<string> Validate(string password, string? userName = null);
}
