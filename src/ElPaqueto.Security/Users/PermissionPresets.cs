namespace ElPaqueto.Security.Users;

/// <summary>
/// Named permission sets offered by the command line. The flags stay independent: a preset is only a
/// shorthand for a combination of them.
/// </summary>
internal static class PermissionPresets
{
    /// <summary><c>reader</c>: browse and download packages.</summary>
    public const Permission Reader = Permission.Read;

    /// <summary><c>publisher</c>: browse, download and publish packages.</summary>
    public const Permission Publisher = Permission.Read | Permission.Publish;

    /// <summary><c>administrator</c>: every permission. Created by <c>admin bootstrap</c>.</summary>
    public const Permission Administrator = Permission.Read | Permission.Publish | Permission.Admin;
}
