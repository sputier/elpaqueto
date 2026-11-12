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
}
