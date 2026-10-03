using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteDesktop.Platform.Windows;

[SupportedOSPlatform("windows")]
public static partial class WindowsProcess
{
    private static readonly nint PerMonitorAwareV2 = -4;

    /// <summary>
    /// Makes DXGI, GetSystemMetrics and SendInput all speak physical pixels. Without it Windows virtualizes
    /// coordinates for scaled displays (e.g. 2560 px reported as 1707 at 150 %), and captured frames, display bounds
    /// and injected pointer positions no longer agree. Processes with a manifest (the client) already have it;
    /// headless hosts call this first thing at startup. Calling it again is harmless.
    /// </summary>
    public static void EnablePerMonitorDpiAwareness() => _ = SetProcessDpiAwarenessContext(PerMonitorAwareV2);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(nint value);
}
