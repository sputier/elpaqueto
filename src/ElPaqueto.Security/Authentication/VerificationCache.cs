using System.Security.Cryptography;
using System.Text;
using ElPaqueto.Security.Configuration;
using ElPaqueto.Security.Data;
using ElPaqueto.Security.Passwords;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Authentication;

/// <summary>
/// Remembers, briefly and in memory only, that a user's password was verified, so that a client that
/// sends credentials on every request does not pay for a slow password hash each time.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>There is <b>one entry per user</b>, so the size is bounded by the number of users and cannot be
/// filled by spelling the same password in different ways.</item>
/// <item>The entry holds <c>HMAC-SHA256(per-process random key, stored password hash, normalized password)</c>,
/// compared in fixed time: the password is not stored in any reversible form, the value means nothing
/// outside this process, and including the stored hash invalidates the entry as soon as the password changes.</item>
/// <item>Only successes are cached, never failures.</item>
/// <item>The user's state and permissions are not cached: the handler reads them on every request.</item>
/// </list>
/// </remarks>
internal sealed class VerificationCache : IDisposable
{
    private const int MaximumEntries = 10_000;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaximumEntries });
    private readonly byte[] _processKey = RandomNumberGenerator.GetBytes(32);
    private readonly TimeSpan _timeToLive;

    public VerificationCache(IOptions<ElPaquetoSecurityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _timeToLive = options.Value.Authentication.VerificationCacheTtl;
    }

    public bool Contains(User user, string password)
    {
        if (_timeToLive <= TimeSpan.Zero || !_cache.TryGetValue(user.Id, out byte[]? remembered) || remembered is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(remembered, DigestFor(user, password));
    }

    public void Remember(User user, string password)
    {
        if (_timeToLive <= TimeSpan.Zero)
        {
            return;
        }

        _cache.Set(
            user.Id,
            DigestFor(user, password),
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = _timeToLive });
    }

    public void Dispose()
    {
        _cache.Dispose();
    }

    private byte[] DigestFor(User user, string password)
    {
        var data = Encoding.UTF8.GetBytes($"{user.PasswordHash}\0{PasswordNormalization.Normalize(password)}");

        try
        {
            return HMACSHA256.HashData(_processKey, data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }
}
