namespace ElPaqueto.Security.ApiKeys;

/// <summary>
/// Creates, lists, revokes, rotates and validates API keys. Tokens are returned once at creation and
/// are never stored or logged; only the SHA-256 of their secret part is persisted.
/// </summary>
/// <remarks>Scoped: it uses the scoped security database context.</remarks>
public interface IApiKeyService
{
    /// <summary>
    /// Creates a key for an active user.
    /// </summary>
    /// <param name="ownerUserId">The owner.</param>
    /// <param name="name">A label, 1 to 100 characters, no control characters.</param>
    /// <param name="permissions">
    /// The permissions of the key, stated explicitly: at least one flag, all held by the owner now.
    /// <see cref="Permission.Admin"/> is granted only when it is part of this value.
    /// </param>
    /// <param name="expiresAt">The expiry (UTC, in the future), or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The key and its token, shown this once.</returns>
    /// <exception cref="ApiKeyRequestException">The request is refused.</exception>
    public Task<ApiKeyCreated> CreateAsync(
        Guid ownerUserId,
        string name,
        Permission permissions,
        DateTime? expiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists keys, revoked and expired ones included, oldest first.
    /// </summary>
    /// <param name="ownerUserId">Only the keys of this user, or all keys when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The keys, without secrets.</returns>
    public Task<IReadOnlyList<ApiKeyInfo>> ListAsync(Guid? ownerUserId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds one key, revoked and expired ones included.
    /// </summary>
    /// <param name="keyId">The public key id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The key, without its secret, or <see langword="null"/> when no key has this id.</returns>
    public Task<ApiKeyInfo?> FindAsync(string keyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a key. Idempotent.
    /// </summary>
    /// <param name="keyId">The public key id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What happened.</returns>
    public Task<ApiKeyRevocation> RevokeAsync(string keyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a key: creates a new one with the same owner, name and permissions and revokes the old
    /// one, in one transaction. If the new key cannot be stored, the old one stays valid.
    /// </summary>
    /// <param name="keyId">The public key id of the key to replace.</param>
    /// <param name="expiresAt">The expiry of the new key (UTC, in the future), or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The new key and its token, shown this once.</returns>
    /// <exception cref="ApiKeyRequestException">
    /// The key does not exist or is revoked, or the request is refused as by <see cref="CreateAsync"/>.
    /// </exception>
    public Task<ApiKeyCreated> RotateAsync(string keyId, DateTime? expiresAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a token as received from a client. A malformed or oversized value is refused without
    /// any database access. On success the owner is the authenticated user and
    /// <see cref="ApiKeyValidationResult.EffectivePermissions"/> are the key's permissions intersected
    /// with the owner's current ones.
    /// </summary>
    /// <param name="token">The token, untrusted.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome. Its failure reason is for logs only, never for the response.</returns>
    public Task<ApiKeyValidationResult> ValidateAsync(string? token, CancellationToken cancellationToken = default);
}
