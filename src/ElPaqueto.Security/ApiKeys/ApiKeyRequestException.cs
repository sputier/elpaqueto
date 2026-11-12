namespace ElPaqueto.Security.ApiKeys;

/// <summary>
/// A request to create or rotate a key was refused (unknown or disabled owner, permissions the
/// owner does not hold, invalid name or expiry). The message is safe to show to an administrator.
/// </summary>
public sealed class ApiKeyRequestException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ApiKeyRequestException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The reason; never contains a token.</param>
    public ApiKeyRequestException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The reason; never contains a token.</param>
    /// <param name="innerException">The cause.</param>
    public ApiKeyRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
