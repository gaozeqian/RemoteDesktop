using System.Runtime.InteropServices;
using RemoteDesktop.Abstractions.Platform;

namespace RemoteDesktop.Platform.Windows;

/// <summary>
/// Keeps Windows awake while a controller is connected using <c>SetThreadExecutionState</c>.
/// </summary>
public sealed partial class WindowsKeepAwake : ISystemKeepAwake
{
    private int _count;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint SetThreadExecutionState(uint esFlags);

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsAwayModeRequired = 0x00000040;

    public void Acquire()
    {
        if (Interlocked.Increment(ref _count) == 1)
        {
            SetThreadExecutionState(EsContinuous | EsSystemRequired | EsAwayModeRequired);
        }
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _count) <= 0)
        {
            Interlocked.Exchange(ref _count, 0);
            SetThreadExecutionState(EsContinuous);
        }
    }
}
