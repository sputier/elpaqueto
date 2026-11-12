namespace ElPaqueto.Security.Cli;

/// <summary>
/// The command-line spelling of permissions: lower-case flag names.
/// </summary>
internal static class PermissionText
{
    private static readonly (Permission Flag, string Name)[] Flags =
    [
        (Permission.Read, "read"),
        (Permission.Publish, "publish"),
        (Permission.Admin, "admin"),
    ];

    /// <summary>The names of the flags set in <paramref name="permissions"/>, in a fixed order.</summary>
    public static IReadOnlyList<string> Names(Permission permissions)
        => [.. Flags.Where(f => (permissions & f.Flag) == f.Flag).Select(f => f.Name)];

    /// <summary>A comma-separated list of the flags, or <c>none</c>.</summary>
    public static string Format(Permission permissions)
    {
        var names = Names(permissions);
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }
}
