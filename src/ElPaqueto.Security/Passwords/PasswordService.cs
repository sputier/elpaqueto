using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// Passwords are normalized to Unicode NFKC before hashing and verifying (NIST SP 800-63B) so the
/// same password typed on different systems verifies. Input is bounded before normalization.
/// </remarks>
internal sealed class PasswordService : IPasswordService
{
    // Generous bound applied before normalization: NFKC can lengthen a string, and the policy
    // limit is applied after it. Anything longer is never hashed in full.
    private const int MaximumRawLength = 1024;

    private static readonly User Placeholder = new();

    private readonly IPasswordHasher<User> _hasher;
    private readonly SecurityPasswordOptions _options;
    private readonly string _dummyHash;

    public PasswordService(IPasswordHasher<User> hasher, IOptions<ElPaquetoSecurityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(options);

        _hasher = hasher;
        _options = options.Value.Passwords;

        // Made once with the current parameters: verifying against it costs the same as verifying
        // a real hash, which is what keeps unknown users indistinguishable from known ones.
        _dummyHash = _hasher.HashPassword(Placeholder, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        return _hasher.HashPassword(Placeholder, Normalize(password));
    }

    public PasswordVerification Verify(User? user, string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var normalized = Normalize(password);

        if (user is null
            || !user.IsActive
            || string.IsNullOrEmpty(user.PasswordHash)
            || LengthOf(normalized) > SecurityPasswordOptions.MaximumLength)
        {
            // Same amount of work as a real verification, on a bounded input.
            _ = _hasher.VerifyHashedPassword(Placeholder, _dummyHash, Truncate(normalized));
            return PasswordVerification.Failed;
        }

        return _hasher.VerifyHashedPassword(user, user.PasswordHash, normalized) switch
        {
            PasswordVerificationResult.Success => PasswordVerification.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.SuccessRehashNeeded,
            _ => PasswordVerification.Failed,
        };
    }

    public IReadOnlyList<string> Validate(string password, string? userName = null)
    {
        ArgumentNullException.ThrowIfNull(password);

        var errors = new List<string>();
        var normalized = Normalize(password);
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
            && string.Equals(normalized, Normalize(userName), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("The password must not be the same as the user name.");
        }

        return errors;
    }

    private static string Normalize(string value)
    {
        var bounded = value.Length > MaximumRawLength ? value[..MaximumRawLength] : value;

        return bounded.Normalize(NormalizationForm.FormKC);
    }

    private static int LengthOf(string value) => value.EnumerateRunes().Count();

    private static string Truncate(string value)
        => value.Length > SecurityPasswordOptions.MaximumLength ? value[..SecurityPasswordOptions.MaximumLength] : value;
}
