using System.Runtime.CompilerServices;

namespace RemoteDesktop.Platform.Windows.Tests;

internal static class DpiAwareness
{
    // Same as the real hosts: physical pixels everywhere.
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsProcess.EnablePerMonitorDpiAwareness();
        }
    }
}
