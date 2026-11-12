using System.Text.RegularExpressions;
using ElPaqueto.Security.Data;

namespace ElPaqueto.Security.Users;

/// <summary>
/// The rules for user names: 1 to 64 characters from <c>A-Z a-z 0-9 . _ -</c>, unique ignoring case
/// (through <see cref="User.Normalize"/> and the unique index on the normalized name).
/// </summary>
/// <remarks>
/// The character set excludes <c>:</c> (the Basic credentials separator), whitespace, control characters
/// and look-alike Unicode letters, so a user name reads and compares the same everywhere.
/// </remarks>
internal static partial class UserNames
{
    /// <summary>Returns why <paramref name="userName"/> is not a valid user name, or <see langword="null"/>.</summary>
    /// <param name="userName">The candidate user name.</param>
    /// <returns>An error message that does not repeat the value, or <see langword="null"/> when it is valid.</returns>
    public static string? Validate(string? userName)
    {
        if (string.IsNullOrEmpty(userName))
        {
            return "The user name is required.";
        }

        if (userName.Length > User.MaxUserNameLength)
        {
            return $"The user name must be at most {User.MaxUserNameLength} characters long.";
        }

        return Allowed().IsMatch(userName)
            ? null
            : "The user name may only contain the letters A-Z and a-z, the digits 0-9, '.', '_' and '-'.";
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Allowed();
}
