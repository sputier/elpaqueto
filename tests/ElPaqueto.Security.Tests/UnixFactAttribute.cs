namespace ElPaqueto.Security.Tests;

/// <summary>
/// A fact that is reported as skipped, with its reason, on Windows: Unix file modes do not exist
/// there. It runs on the Linux leg of the CI.
/// </summary>
internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix file modes do not exist on Windows; this test runs on Linux.";
        }
    }
}
