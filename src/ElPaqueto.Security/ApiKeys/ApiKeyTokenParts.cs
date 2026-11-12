namespace ElPaqueto.Security.ApiKeys;

/// <summary>
/// A parsed API key token. <see cref="ToString"/> never includes the secret, so the value can be
/// logged or interpolated by mistake without leaking it.
/// </summary>
public readonly struct ApiKeyTokenParts : IEquatable<ApiKeyTokenParts>
{
    internal ApiKeyTokenParts(string keyId, string secret)
    {
        KeyId = keyId;
        Secret = secret;
    }

    /// <summary>The public key identifier.</summary>
    public string KeyId { get; }

    /// <summary>The secret. Never log it.</summary>
    internal string Secret { get; }

    /// <summary>Compares two parsed tokens.</summary>
    /// <param name="left">The first token.</param>
    /// <param name="right">The second token.</param>
    /// <returns>Whether both are equal.</returns>
    public static bool operator ==(ApiKeyTokenParts left, ApiKeyTokenParts right) => left.Equals(right);

    /// <summary>Compares two parsed tokens.</summary>
    /// <param name="left">The first token.</param>
    /// <param name="right">The second token.</param>
    /// <returns>Whether both differ.</returns>
    public static bool operator !=(ApiKeyTokenParts left, ApiKeyTokenParts right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(ApiKeyTokenParts other)
        => string.Equals(KeyId, other.KeyId, StringComparison.Ordinal)
        && string.Equals(Secret, other.Secret, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ApiKeyTokenParts other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(KeyId ?? string.Empty);

    /// <summary>Returns the key id with a redacted secret.</summary>
    /// <returns>A loggable representation.</returns>
    public override string ToString() => $"{ApiKeyToken.Prefix}{KeyId}_***";
}
