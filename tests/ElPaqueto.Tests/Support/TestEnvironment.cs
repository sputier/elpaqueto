using System;
using System.Runtime.CompilerServices;

namespace ElPaqueto.Tests;

/// <summary>
/// Process-wide settings for the integration tests.
/// </summary>
internal static class TestEnvironment
{
    /// <summary>
    /// Every test host watches its configuration files, and each watcher takes an inotify instance on
    /// Linux, where the default per-user limit is 128. The suite creates hundreds of hosts: use the
    /// polling file watcher instead, which needs no inotify instance. It must be set before the first host is built.
    /// </summary>
    [ModuleInitializer]
    internal static void UsePollingFileWatcher()
    {
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "true");
    }
}
