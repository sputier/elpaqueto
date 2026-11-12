namespace ElPaqueto.Security.Tests;

/// <summary>
/// A fact that needs Unix file modes to deny access: skipped on Windows, and when the tests run as root,
/// which reads any file whatever its mode.
/// </summary>
internal sealed class UnprivilegedUnixFactAttribute : FactAttribute
{
    public UnprivilegedUnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix file modes do not exist on Windows; this test runs on Linux.";
        }
        else if (Environment.IsPrivilegedProcess)
        {
            Skip = "Root reads files whatever their mode; this test needs an unprivileged user.";
        }
    }
}
