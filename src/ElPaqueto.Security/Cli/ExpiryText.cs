using System.Globalization;

namespace ElPaqueto.Security.Cli;

/// <summary>
/// The command-line spelling of an API key expiry: <c>--expires &lt;when&gt;</c> or <c>--no-expiry</c>,
/// exactly one of them.
/// </summary>
internal static class ExpiryText
{
    public const string ExpiresOption = "--expires";
    public const string NoExpiryOption = "--no-expiry";

    /// <summary>The longest relative expiry accepted, in days (about 100 years).</summary>
    public const int MaximumDays = 36500;

    public static IReadOnlyList<AdminOption> Options { get; } =
    [
        new(ExpiresOption, "when", "Expiry: a number of days (90d), a UTC date (2027-01-31, at 00:00 UTC) or a UTC time (2027-01-31T12:00:00Z)."),
        new(NoExpiryOption, null, "The key never expires. One of --expires and --no-expiry is required."),
    ];

    /// <summary>
    /// Reads the expiry choice. <paramref name="expiresAt"/> is <see langword="null"/> for <c>--no-expiry</c>.
    /// </summary>
    /// <param name="arguments">The parsed arguments.</param>
    /// <param name="now">The current UTC time: the expiry must be after it.</param>
    /// <param name="expiresAt">The expiry (UTC), or <see langword="null"/> for none.</param>
    /// <param name="error">Why the choice is refused.</param>
    /// <returns>Whether exactly one valid choice was made.</returns>
    public static bool TryRead(ParsedArguments arguments, DateTime now, out DateTime? expiresAt, out string error)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        expiresAt = null;
        var value = arguments.Value(ExpiresOption);
        var noExpiry = arguments.Has(NoExpiryOption);

        if (value is null && !noExpiry)
        {
            error = $"State the expiry of the key: '{ExpiresOption} <when>' (for example {ExpiresOption} 90d) or '{NoExpiryOption}'.";
            return false;
        }

        if (value is not null && noExpiry)
        {
            error = $"The options '{ExpiresOption}' and '{NoExpiryOption}' cannot be combined.";
            return false;
        }

        if (noExpiry)
        {
            error = string.Empty;
            return true;
        }

        if (!TryParse(value!, now, out var parsed))
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"The option '{ExpiresOption}' accepts a number of days from 1 to {MaximumDays} (90d), a date (2027-01-31) or a UTC time (2027-01-31T12:00:00Z).");
            return false;
        }

        if (parsed <= now)
        {
            error = $"The expiry given with '{ExpiresOption}' ({Format(parsed)}) is not in the future.";
            return false;
        }

        expiresAt = parsed;
        error = string.Empty;
        return true;
    }

    /// <summary>A UTC time as the commands print it: <c>2027-01-31 00:00:00 UTC</c>, or <c>never</c>.</summary>
    public static string Format(DateTime? value)
        => value is { } time
            ? DateTime.SpecifyKind(time, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
            : "never";

    private static bool TryParse(string value, DateTime now, out DateTime expiresAt)
    {
        expiresAt = default;

        if (value.Length is >= 2 and <= 6
            && (value[^1] == 'd' || value[^1] == 'D')
            && value[..^1].All(char.IsAsciiDigit)
            && int.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            && days is >= 1 and <= MaximumDays)
        {
            expiresAt = now.AddDays(days);
            return true;
        }

        if (DateTime.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var date))
        {
            expiresAt = DateTime.SpecifyKind(date, DateTimeKind.Utc);
            return true;
        }

        // An explicit offset or Z is required: a time without one would depend on the machine's time zone.
        if (DateTimeOffset.TryParseExact(
            value,
            ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mmzzz"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var time))
        {
            expiresAt = time.UtcDateTime;
            return true;
        }

        return false;
    }
}
