using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using ElPaqueto.Security.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ElPaqueto.Security.ApiKeys;

/// <summary>
/// API keys stored in the security database. See <c>docs/technical/api-keys.md</c>.
/// </summary>
internal sealed partial class ApiKeyService(
    AuthDbContext context,
    TimeProvider timeProvider,
    IApiKeyHashComparer comparer,
    ApiKeyUsageTracker usageTracker,
    ILogger<ApiKeyService> logger) : IApiKeyService
{
    private const Permission DefinedPermissions = Permission.Read | Permission.Publish | Permission.Admin;

    // Compared against when the key id is unknown, so that path does the same work as a wrong secret.
    private static readonly byte[] DummyHash = RandomNumberGenerator.GetBytes(ApiKey.SecretHashLength);

    private readonly ApiKeyGenerator _generator = ApiKeyGenerator.Default;

    public async Task<ApiKeyCreated> CreateAsync(
        Guid ownerUserId,
        string name,
        Permission permissions,
        DateTime? expiresAt,
        CancellationToken cancellationToken = default)
    {
        var now = UtcNow();
        ValidateName(name);
        var expiry = ValidateExpiry(expiresAt, now);

        var owner = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == ownerUserId, cancellationToken)
            ?? throw new ApiKeyRequestException("The owner does not exist.");

        var (key, token) = NewKey(owner, name, permissions, expiry, now);
        await InsertAsync(key, cancellationToken);

        LogCreated(logger, key.KeyId, owner.Id, key.Permissions);
        return new ApiKeyCreated(ToInfo(key), token);
    }

    public async Task<IReadOnlyList<ApiKeyInfo>> ListAsync(Guid? ownerUserId = null, CancellationToken cancellationToken = default)
    {
        var query = context.ApiKeys.AsNoTracking();
        if (ownerUserId is { } owner)
        {
            query = query.Where(k => k.OwnerUserId == owner);
        }

        var keys = await query.OrderBy(k => k.CreatedAt).ThenBy(k => k.KeyId).ToListAsync(cancellationToken);
        return keys.Select(ToInfo).ToList();
    }

    public async Task<ApiKeyRevocation> RevokeAsync(string keyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyId);

        if (!IsKeyIdFormat(keyId))
        {
            return ApiKeyRevocation.NotFound;
        }

        var now = UtcNow();
        var stamp = Guid.NewGuid().ToString("N");

        // One conditional statement: two concurrent revocations cannot both report success, and the
        // first revocation time is never overwritten.
        var updated = await context.ApiKeys
            .Where(k => k.KeyId == keyId && k.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(k => k.RevokedAt, now)
                    .SetProperty(k => k.ConcurrencyStamp, stamp),
                cancellationToken);

        if (updated == 1)
        {
            LogRevoked(logger, keyId);
            return ApiKeyRevocation.Revoked;
        }

        return await context.ApiKeys.AnyAsync(k => k.KeyId == keyId, cancellationToken)
            ? ApiKeyRevocation.AlreadyRevoked
            : ApiKeyRevocation.NotFound;
    }

    public async Task<ApiKeyCreated> RotateAsync(string keyId, DateTime? expiresAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyId);

        var now = UtcNow();
        var expiry = ValidateExpiry(expiresAt, now);

        if (!IsKeyIdFormat(keyId))
        {
            throw new ApiKeyRequestException("The key does not exist.");
        }

        // Nothing is committed unless both the new key is stored and the old one is revoked. Two concurrent
        // rotations of the same key cannot both succeed: the revocation below is conditional, and the loser
        // fails and rolls back its new key.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var old = await context.ApiKeys
            .AsNoTracking()
            .Where(k => k.KeyId == keyId)
            .Select(k => new { k.Name, k.Permissions, k.RevokedAt, k.Owner })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ApiKeyRequestException("The key does not exist.");

        if (old.RevokedAt is not null)
        {
            throw new ApiKeyRequestException("The key is revoked: create a new key instead.");
        }

        var (key, token) = NewKey(old.Owner!, old.Name, old.Permissions, expiry, now);
        await InsertAsync(key, cancellationToken);

        var stamp = Guid.NewGuid().ToString("N");
        var revoked = await context.ApiKeys
            .Where(k => k.KeyId == keyId && k.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(k => k.RevokedAt, now)
                    .SetProperty(k => k.ConcurrencyStamp, stamp),
                cancellationToken);

        if (revoked != 1)
        {
            // Another rotation or revocation won the race; the new key is rolled back on dispose.
            throw new ApiKeyRequestException("The key was revoked concurrently: create a new key instead.");
        }

        await transaction.CommitAsync(cancellationToken);

        LogRotated(logger, keyId, key.KeyId);
        return new ApiKeyCreated(ToInfo(key), token);
    }

    public async Task<ApiKeyValidationResult> ValidateAsync(string? token, CancellationToken cancellationToken = default)
    {
        // Checked first, on the length alone: a huge value is never scanned.
        if (token is not null && token.Length > ApiKeyToken.MaximumInputLength)
        {
            return Refuse(ApiKeyValidationFailure.Oversized);
        }

        if (!ApiKeyToken.TryParse(token, out var parts))
        {
            return Refuse(ApiKeyValidationFailure.Malformed);
        }

        var computed = ApiKeySecretHash.Compute(parts.Secret);

        var row = await context.ApiKeys
            .AsNoTracking()
            .Where(k => k.KeyId == parts.KeyId)
            .Select(k => new
            {
                k.Id,
                k.SecretHash,
                k.Permissions,
                k.ExpiresAt,
                k.RevokedAt,
                k.LastUsedAt,
                k.OwnerUserId,
                OwnerUserName = k.Owner!.UserName,
                OwnerIsActive = k.Owner.IsActive,
                OwnerPermissions = k.Owner.Permissions,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            // Same comparison work as for a known key: timing does not reveal which key ids exist.
            _ = comparer.HashesEqual(computed, DummyHash);
            return Refuse(ApiKeyValidationFailure.UnknownKey, parts.KeyId);
        }

        // The state of the key is only considered once the secret is proven.
        if (!comparer.HashesEqual(computed, row.SecretHash))
        {
            return Refuse(ApiKeyValidationFailure.WrongSecret, parts.KeyId);
        }

        var now = UtcNow();

        if (row.RevokedAt is not null)
        {
            return Refuse(ApiKeyValidationFailure.Revoked, parts.KeyId);
        }

        if (row.ExpiresAt is { } expires && expires <= now)
        {
            return Refuse(ApiKeyValidationFailure.Expired, parts.KeyId);
        }

        if (!row.OwnerIsActive)
        {
            return Refuse(ApiKeyValidationFailure.OwnerDisabled, parts.KeyId);
        }

        // Re-intersected on every use: removing a permission from the owner restricts every key at once.
        var effective = row.Permissions & row.OwnerPermissions & DefinedPermissions;

        if (usageTracker.TryClaimWrite(row.Id, row.LastUsedAt, now))
        {
            await RecordUseAsync(row.Id, parts.KeyId, now, cancellationToken);
        }

        return ApiKeyValidationResult.Success(row.Id, parts.KeyId, row.OwnerUserId, row.OwnerUserName, effective);
    }

    private static bool IsKeyIdFormat(string keyId)
        => keyId.Length == ApiKeyToken.KeyIdLength && keyId.All(c => ApiKeyGenerator.Alphabet.Contains(c, StringComparison.Ordinal));

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ApiKeyRequestException("The key name is required.");
        }

        if (name.Length > ApiKey.MaxNameLength)
        {
            throw new ApiKeyRequestException(string.Create(
                CultureInfo.InvariantCulture,
                $"The key name must be at most {ApiKey.MaxNameLength} characters long."));
        }

        if (name.Any(char.IsControl))
        {
            throw new ApiKeyRequestException("The key name must not contain control characters.");
        }
    }

    private static DateTime? ValidateExpiry(DateTime? expiresAt, DateTime now)
    {
        if (expiresAt is not { } value)
        {
            return null;
        }

        // Same rule as the database converter: an unspecified kind is taken as UTC.
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        if (utc <= now)
        {
            throw new ApiKeyRequestException("The expiry must be in the future.");
        }

        return utc;
    }

    private static ApiKeyInfo ToInfo(ApiKey key) => new(
        key.Id,
        key.KeyId,
        key.OwnerUserId,
        key.Name,
        key.Permissions,
        key.CreatedAt,
        key.ExpiresAt,
        key.RevokedAt,
        key.LastUsedAt);

    private (ApiKey Key, string Token) NewKey(User owner, string name, Permission permissions, DateTime? expiresAt, DateTime now)
    {
        if (!owner.IsActive)
        {
            throw new ApiKeyRequestException("The owner is disabled.");
        }

        if ((permissions & ~DefinedPermissions) != 0)
        {
            throw new ApiKeyRequestException("The permissions contain undefined flags.");
        }

        if (permissions == Permission.None)
        {
            throw new ApiKeyRequestException("A key needs at least one permission.");
        }

        // Admin included: a key never carries a flag its owner does not hold, and only the flags asked for.
        var missing = permissions & ~owner.Permissions;
        if (missing != Permission.None)
        {
            throw new ApiKeyRequestException(string.Create(
                CultureInfo.InvariantCulture,
                $"The owner does not hold these permissions: {missing}."));
        }

        var (keyId, secret) = _generator.GenerateToken();
        var key = new ApiKey
        {
            Id = Guid.NewGuid(),
            KeyId = keyId,
            SecretHash = ApiKeySecretHash.Compute(secret),
            OwnerUserId = owner.Id,
            Name = name,
            Permissions = permissions,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };

        return (key, ApiKeyToken.Format(keyId, secret));
    }

    // The service never leaves its own entities tracked: the scoped context may be reused by the caller,
    // and a stale tracked key would shadow later set-based updates (revocation, last use).
    private async Task InsertAsync(ApiKey key, CancellationToken cancellationToken)
    {
        var entry = context.ApiKeys.Add(key);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            entry.State = EntityState.Detached;
        }
    }

    private async Task RecordUseAsync(Guid id, string keyId, DateTime now, CancellationToken cancellationToken)
    {
        var staleBefore = usageTracker.StaleBefore(now);

        try
        {
            // Conditional: another process that wrote within the interval wins, and this is a no-op.
            // Not through the change tracker, so the concurrency stamp is untouched and a concurrent
            // revocation or rotation never conflicts with a mere use.
            await context.ApiKeys
                .Where(k => k.Id == id && (k.LastUsedAt == null || k.LastUsedAt <= staleBefore))
                .ExecuteUpdateAsync(setters => setters.SetProperty(k => k.LastUsedAt, now), cancellationToken);
        }
        catch (Exception e) when (e is DbException or DbUpdateException)
        {
            // Bookkeeping only: a busy database must not turn a valid key into a refusal.
            usageTracker.ReleaseClaim(id, now);
            LogLastUseNotRecorded(logger, keyId, e.GetType().Name);
        }
        catch (OperationCanceledException)
        {
            usageTracker.ReleaseClaim(id, now);
            throw;
        }
    }

    private ApiKeyValidationResult Refuse(ApiKeyValidationFailure failure, string? keyId = null)
    {
        LogRefused(logger, failure, keyId ?? "(none)");
        return ApiKeyValidationResult.Fail(failure, keyId);
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    [LoggerMessage(Level = LogLevel.Information, Message = "API key {KeyId} created for user {OwnerUserId} with permissions {Permissions}.")]
    private static partial void LogCreated(ILogger logger, string keyId, Guid ownerUserId, Permission permissions);

    [LoggerMessage(Level = LogLevel.Information, Message = "API key {KeyId} revoked.")]
    private static partial void LogRevoked(ILogger logger, string keyId);

    [LoggerMessage(Level = LogLevel.Information, Message = "API key {OldKeyId} rotated: replaced by {NewKeyId} and revoked.")]
    private static partial void LogRotated(ILogger logger, string oldKeyId, string newKeyId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "API key refused: {Reason} (key id {KeyId}).")]
    private static partial void LogRefused(ILogger logger, ApiKeyValidationFailure reason, string keyId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The last use of API key {KeyId} could not be recorded ({ErrorType}). The key was accepted.")]
    private static partial void LogLastUseNotRecorded(ILogger logger, string keyId, string errorType);
}
