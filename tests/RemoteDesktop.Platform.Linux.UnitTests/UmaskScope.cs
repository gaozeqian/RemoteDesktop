using System.Runtime.InteropServices;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// Sets the process umask for the duration of a test and restores it afterwards. The umask is what turns the mode
/// passed to open(2) into the mode a new file gets, so running with it fully open (000) is how a test sees whether a
/// file was created with the intended mode, rather than only ending up with it after a later chmod.
/// </summary>
/// <remarks>The umask is process-wide; other tests only ever see a wider default for the instant this is in force.</remarks>
internal sealed class UmaskScope : IDisposable
{
    private readonly uint _previous;

    public UmaskScope(uint mask) => _previous = Umask(mask);

    public void Dispose() => _ = Umask(_previous);

#pragma warning disable SYSLIB1054 // A single blittable call; LibraryImport would need AllowUnsafeBlocks for no gain.
    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint Umask(uint mask);
#pragma warning restore SYSLIB1054
}
