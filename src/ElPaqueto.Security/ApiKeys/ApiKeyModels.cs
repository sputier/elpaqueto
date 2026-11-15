namespace ElPaqueto.Security.ApiKeys;

/// <summary>
/// What may be shown about an API key. Never contains the secret or its hash.
/// </summary>
/// <param name="Id">The internal identifier.</param>
/// <param name="KeyId">The public key identifier, part of the token.</param>
/// <param name="OwnerUserId">The owner.</param>
/// <param name="Name">The label.</param>
/// <param name="Permissions">The permissions requested for the key (before intersection with the owner's).</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
/// <param name="ExpiresAt">Expiry time (UTC), if any.</param>
/// <param name="RevokedAt">Revocation time (UTC), if revoked.</param>
/// <param name="LastUsedAt">Last successful use (UTC), with the granularity of <c>Security:ApiKeys:LastUsedUpdateInterval</c>.</param>
public sealed record ApiKeyInfo(
    Guid Id,
    string KeyId,
    Guid OwnerUserId,
    string Name,
    Permission Permissions,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    DateTime? RevokedAt,
    DateTime? LastUsedAt);

/// <summary>
/// A newly created key and its token. The token is returned this once and is not stored anywhere.
/// </summary>
public sealed class ApiKeyCreated
{
    internal ApiKeyCreated(ApiKeyInfo key, string token)
    {
        Key = key;
        Token = token;
    }

    /// <summary>The key.</summary>
    public ApiKeyInfo Key { get; }

    /// <summary>The complete token (<c>epq_…</c>). Show it to the owner once; never log it.</summary>
    public string Token { get; }

    /// <summary>Does not include the token.</summary>
    /// <returns>A loggable representation.</returns>
    public override string ToString() => $"API key {Key.KeyId}";
}

/// <summary>
/// Why a token was refused. For logs and diagnostics only: an HTTP response must not reveal it.
/// </summary>
public enum ApiKeyValidationFailure
{
    /// <summary>The token is valid.</summary>
    None = 0,

    /// <summary>The value is longer than <see cref="ApiKeyToken.MaximumInputLength"/>. Not processed further.</summary>
    Oversized,

    /// <summary>The value does not have the token format. The database was not queried.</summary>
    Malformed,

    /// <summary>No key has this key id.</summary>
    UnknownKey,

    /// <summary>The key id exists but the secret is wrong.</summary>
    WrongSecret,

    /// <summary>The key was revoked.</summary>
    Revoked,

    /// <summary>The key has expired.</summary>
    Expired,

    /// <summary>The owner of the key is disabled.</summary>
    OwnerDisabled,
}

/// <summary>
/// The outcome of validating a token.
/// </summary>
public sealed class ApiKeyValidationResult
{
    private ApiKeyValidationResult(
        ApiKeyValidationFailure failure,
        Guid apiKeyId,
        string? keyId,
        Guid ownerUserId,
        string? ownerUserName,
        Permission effectivePermissions)
    {
        Failure = failure;
        ApiKeyId = apiKeyId;
        KeyId = keyId;
        OwnerUserId = ownerUserId;
        OwnerUserName = ownerUserName;
        EffectivePermissions = effectivePermissions;
    }

    /// <summary>Whether the token authenticates its owner.</summary>
    public bool Succeeded => Failure == ApiKeyValidationFailure.None;

    /// <summary>The internal reason of a refusal; <see cref="ApiKeyValidationFailure.None"/> on success.</summary>
    public ApiKeyValidationFailure Failure { get; }

    /// <summary>The key's internal identifier, on success.</summary>
    public Guid ApiKeyId { get; }

    /// <summary>The public key id: on success, and on failures after parsing (never the secret).</summary>
    public string? KeyId { get; }

    /// <summary>The owner, on success.</summary>
    public Guid OwnerUserId { get; }

    /// <summary>The owner's user name, on success.</summary>
    public string? OwnerUserName { get; }

    /// <summary>
    /// On success, the key's permissions intersected with the owner's current permissions (possibly
    /// <see cref="Permission.None"/>); <see cref="Permission.None"/> on failure.
    /// </summary>
    public Permission EffectivePermissions { get; }

    internal static ApiKeyValidationResult Fail(ApiKeyValidationFailure failure, string? keyId = null)
        => new(failure, Guid.Empty, keyId, Guid.Empty, null, Permission.None);

    internal static ApiKeyValidationResult Success(
        Guid apiKeyId,
        string keyId,
        Guid ownerUserId,
        string ownerUserName,
        Permission effectivePermissions)
        => new(ApiKeyValidationFailure.None, apiKeyId, keyId, ownerUserId, ownerUserName, effectivePermissions);
}

/// <summary>
/// The outcome of a revocation.
/// </summary>
public enum ApiKeyRevocation
{
    /// <summary>No key has this key id.</summary>
    NotFound = 0,

    /// <summary>The key is now revoked.</summary>
    Revoked = 1,

    /// <summary>The key was already revoked; nothing changed.</summary>
    AlreadyRevoked = 2,
}
