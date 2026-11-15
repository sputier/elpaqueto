using System.Globalization;
using System.Security.Cryptography;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Configuration;
using ElPaqueto.Security.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Passwords;

/// <summary>
/// Password hashing on top of ASP.NET Core Identity's <see cref="IPasswordHasher{TUser}"/>: PBKDF2
/// with HMAC-SHA512, a random 128-bit salt, and the algorithm and iteration count recorded in the
/// hash itself, so the cost can be raised later without invalidating existing hashes.
/// </summary>
/// <remarks>
/// Passwords are normalized to Unicode NFKC before hashing and verifying (NIST SP 800-63B). Input is
/// bounded before normalization. Verification does the same amount of work for every outcome: see
/// <see cref="Verify"/>.
/// </remarks>
internal sealed class PasswordService : IPasswordService
{
    private static readonly User Placeholder = new();

    private readonly IPasswordHasher<User> _hasher;
    private readonly IPasswordTimingPad _timingPad;
    private readonly SecurityPasswordOptions _options;
    private readonly string _dummyHash;

    public PasswordService(
        IPasswordHasher<User> hasher,
        IOptions<ElPaquetoSecurityOptions> options,
        IPasswordTimingPad timingPad)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timingPad);

        _hasher = hasher;
        _timingPad = timingPad;
        _options = options.Value.Passwords;

        // Made once with the current parameters: verifying against it costs the same as verifying
        // a real hash, which is what keeps unknown users indistinguishable from known ones.
        _dummyHash = _hasher.HashPassword(Placeholder, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        return _hasher.HashPassword(Placeholder, PasswordNormalization.Normalize(password));
    }

    /// <remarks>
    /// An unknown or disabled user, a missing hash and an over-length password are verified against a
    /// dummy hash made with the configured cost. A known user whose stored hash is cheaper than the
    /// configured cost is padded with the missing iterations, whatever the outcome, so that raising
    /// the cost does not make dormant accounts distinguishable by their response time. A hash with
    /// a different cost succeeds as <see cref="PasswordVerification.SuccessRehashNeeded"/> in both
    /// directions. A hash that is MORE expensive than the configured cost cannot be made faster: after
    /// the cost is lowered, such accounts keep taking longer until their next login re-hashes them.
    /// </remarks>
    public PasswordVerification Verify(User? user, string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var normalized = PasswordNormalization.Normalize(password);

        if (user is null
            || !user.IsActive
            || string.IsNullOrEmpty(user.PasswordHash)
            || LengthOf(normalized) > SecurityPasswordOptions.MaximumLength)
        {
            // Same amount of work as a real verification, on a bounded input.
            _ = _hasher.VerifyHashedPassword(Placeholder, _dummyHash, Truncate(normalized));
            return PasswordVerification.Failed;
        }

        var outcome = _hasher.VerifyHashedPassword(user, user.PasswordHash, normalized) switch
        {
            PasswordVerificationResult.Success => PasswordVerification.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.SuccessRehashNeeded,
            _ => PasswordVerification.Failed,
        };

        if (PasswordHashFormat.TryGetIterations(user.PasswordHash, out var stored))
        {
            if (stored < _options.IterationCount)
            {
                _timingPad.Pad(_options.IterationCount - stored);
            }

            if (stored != _options.IterationCount && outcome == PasswordVerification.Success)
            {
                outcome = PasswordVerification.SuccessRehashNeeded;
            }
        }

        return outcome;
    }

    public IReadOnlyList<string> Validate(string password, string? userName = null)
    {
        ArgumentNullException.ThrowIfNull(password);

        var errors = new List<string>();
        var normalized = PasswordNormalization.Normalize(password);
        var length = LengthOf(normalized);

        if (length < _options.MinimumLength)
        {
            errors.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The password must be at least {_options.MinimumLength} characters long."));
        }

        if (length > SecurityPasswordOptions.MaximumLength)
        {
            errors.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The password must be at most {SecurityPasswordOptions.MaximumLength} characters long."));
        }

        if (userName is not null
            && string.Equals(normalized, PasswordNormalization.Normalize(userName), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("The password must not be the same as the user name.");
        }

        // A Basic password with the API key format is validated as a key, never as a password: such a
        // password could never be used, and accepting it would blur the two kinds of credentials.
        if (ApiKeyToken.IsTokenFormat(password) || ApiKeyToken.IsTokenFormat(normalized))
        {
            errors.Add("The password must not have the format of an API key (epq_...).");
        }

        return errors;
    }

    private static int LengthOf(string value) => value.EnumerateRunes().Count();

    private static string Truncate(string value)
        => value.Length > SecurityPasswordOptions.MaximumLength ? value[..SecurityPasswordOptions.MaximumLength] : value;
}
