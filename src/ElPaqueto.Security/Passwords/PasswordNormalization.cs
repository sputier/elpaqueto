using System.Text;

namespace ElPaqueto.Security.Passwords;

/// <summary>
/// The one normalization applied to passwords: bounded, then Unicode NFKC (NIST SP 800-63B), so that
/// the same password typed on different systems is the same password everywhere it is used.
/// </summary>
internal static class PasswordNormalization
{
    // Generous bound applied before normalization: NFKC can lengthen a string, and the policy limit
    // is applied after it. Anything longer is never processed in full.
    public const int MaximumRawLength = 1024;

    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var bounded = value.Length > MaximumRawLength ? value[..MaximumRawLength] : value;

        return bounded.Normalize(NormalizationForm.FormKC);
    }
}
