using ElPaqueto.Security.Users;

namespace ElPaqueto.Security.Cli;

/// <summary>
/// The command-line spelling of permissions: lower-case flag names and presets.
/// </summary>
internal static class PermissionText
{
    private static readonly (Permission Flag, string Name)[] Flags =
    [
        (Permission.Read, "read"),
        (Permission.Publish, "publish"),
        (Permission.Admin, "admin"),
    ];

    private static readonly (string Name, Permission Value)[] Presets =
    [
        ("reader", PermissionPresets.Reader),
        ("publisher", PermissionPresets.Publisher),
        ("administrator", PermissionPresets.Administrator),
    ];

    /// <summary>Every accepted token, for help and error messages.</summary>
    public static string ValidValues { get; } =
        $"{string.Join(", ", Flags.Select(f => f.Name))}, or a preset: reader (read), publisher (read, publish), administrator (read, publish, admin)";

    /// <summary>The names of the flags set in <paramref name="permissions"/>, in a fixed order.</summary>
    public static IReadOnlyList<string> Names(Permission permissions)
        => [.. Flags.Where(f => (permissions & f.Flag) == f.Flag).Select(f => f.Name)];

    /// <summary>A comma-separated list of the flags, or <c>none</c>.</summary>
    public static string Format(Permission permissions)
    {
        var names = Names(permissions);
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    /// <summary>
    /// Parses a comma-separated, case-insensitive list of flags and presets. The result is their union.
    /// An empty set is refused: removing every permission is what <c>disable</c> is for.
    /// </summary>
    public static bool TryParse(string? text, out Permission permissions, out string error)
    {
        permissions = Permission.None;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = $"The permission list is empty. Valid values: {ValidValues}. To remove all access, disable the user.";
            return false;
        }

        foreach (var raw in text.Split(','))
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                error = $"The permission list contains an empty entry. Valid values: {ValidValues}.";
                return false;
            }

            var flag = Flags.FirstOrDefault(f => string.Equals(f.Name, token, StringComparison.OrdinalIgnoreCase));
            if (flag.Name is not null)
            {
                permissions |= flag.Flag;
                continue;
            }

            var preset = Presets.FirstOrDefault(p => string.Equals(p.Name, token, StringComparison.OrdinalIgnoreCase));
            if (preset.Name is not null)
            {
                permissions |= preset.Value;
                continue;
            }

            // Repeated only when it is short and cannot be a misplaced API key or password.
            error = token.Length <= 16 && !token.StartsWith("epq_", StringComparison.OrdinalIgnoreCase)
                ? $"Unknown permission '{token}'. Valid values: {ValidValues}."
                : $"Unknown permission. Valid values: {ValidValues}.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
