namespace ElPaqueto.Security.Authentication;

/// <summary>
/// Claim types and values emitted by the ElPaqueto authentication handler.
/// </summary>
/// <remarks>
/// Permissions are carried as claims built once per request by authentication: the authorization
/// side never queries the database.
/// </remarks>
public static class ElPaquetoClaimTypes
{
    /// <summary>One claim per granted <see cref="ElPaqueto.Security.Permission"/> flag; the value is the flag name.</summary>
    public const string Permission = "urn:elpaqueto:permission";

    /// <summary>How the caller proved its identity: <see cref="PasswordMethod"/> or <see cref="ApiKeyMethod"/>.</summary>
    public const string AuthenticationMethod = "urn:elpaqueto:authentication-method";

    /// <summary>The user authenticated with a user name and a password.</summary>
    public const string PasswordMethod = "password";

    /// <summary>The user authenticated with one of its API keys.</summary>
    public const string ApiKeyMethod = "api_key";

    /// <summary>The public key id of the API key that authenticated the request (never the secret).</summary>
    public const string ApiKeyId = "urn:elpaqueto:api-key-id";

    /// <summary>
    /// Where the API key was read from: <see cref="ApiKeyHeaderSource"/> or <see cref="BasicPasswordSource"/>.
    /// Write routes accept only <see cref="ApiKeyHeaderSource"/>.
    /// </summary>
    public const string CredentialSource = "urn:elpaqueto:credential-source";

    /// <summary>The key was sent in the <c>X-NuGet-ApiKey</c> header.</summary>
    public const string ApiKeyHeaderSource = "x-nuget-apikey";

    /// <summary>The key was sent as the password of HTTP Basic credentials.</summary>
    public const string BasicPasswordSource = "basic-password";
}
