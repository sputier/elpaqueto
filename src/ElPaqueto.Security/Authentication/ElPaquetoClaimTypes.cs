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

    /// <summary>How the caller proved its identity: <see cref="PasswordMethod"/>.</summary>
    public const string AuthenticationMethod = "urn:elpaqueto:authentication-method";

    /// <summary>The user authenticated with a user name and a password.</summary>
    public const string PasswordMethod = "password";
}
