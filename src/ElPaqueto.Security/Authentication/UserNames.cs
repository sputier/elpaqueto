namespace ElPaqueto.Security.Authentication;

/// <summary>
/// The rules a user name must follow: 1 to 64 characters from <c>A-Z a-z 0-9 . _ -</c>.
/// </summary>
/// <remarks>
/// RFC 7617 forbids a colon in the user-id; an ASCII allow-list also removes homoglyph and Unicode
/// normalization ambiguity and keeps user names safe to write to logs.
/// </remarks>
internal static class UserNames
{
    /// <summary>Returns whether <paramref name="userName"/> is a valid user name.</summary>
    /// <param name="userName">The candidate user name.</param>
    /// <returns><see langword="true"/> when it follows the rules.</returns>
    public static bool IsValid(string? userName)
    {
        if (string.IsNullOrEmpty(userName) || userName.Length > ElPaqueto.Security.Data.User.MaxUserNameLength)
        {
            return false;
        }

        foreach (var c in userName)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns why <paramref name="userName"/> is not a valid user name, or <see langword="null"/>.</summary>
    /// <param name="userName">The candidate user name.</param>
    /// <returns>An error message that does not repeat the value, or <see langword="null"/> when it is valid.</returns>
    /// <remarks>The verdict is always the one of <see cref="IsValid"/>, so a name accepted here can sign in.</remarks>
    public static string? Validate(string? userName)
    {
        if (IsValid(userName))
        {
            return null;
        }

        if (string.IsNullOrEmpty(userName))
        {
            return "The user name is required.";
        }

        return userName.Length > ElPaqueto.Security.Data.User.MaxUserNameLength
            ? $"The user name must be at most {ElPaqueto.Security.Data.User.MaxUserNameLength} characters long."
            : "The user name may only contain the letters A-Z and a-z, the digits 0-9, '.', '_' and '-'.";
    }
}
