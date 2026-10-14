using System.Runtime.InteropServices;
using RemoteDesktop.Platform.Posix.Interop;

namespace RemoteDesktop.Platform.MacOS.Tests;

/// <summary>
/// Pins the window-size behaviour of the BSD pseudo-terminal straight against the interop layer, with no shell in
/// the way.
/// </summary>
/// <remarks>
/// This is the regression test for the variadic calling convention. <c>ioctl</c> takes its argument variadically and
/// Apple's arm64 ABI passes variadic arguments on the stack, so declaring it the obvious way made the kernel read the
/// size from — and write it to — an unrelated address. Nothing failed loudly: both calls returned success. That is
/// why these tests assert the values that come back rather than the return codes, which is the only thing that would
/// have caught it. The entry points live in the shared Posix layer now, and this test is what keeps the macOS half
/// of it honest.
/// </remarks>
public sealed unsafe class PtyWindowSizeInteropTests
{
    [Fact]
    public void The_window_size_round_trips_through_the_replica()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The BSD pseudo-terminal is macOS only.");

        var (master, replica) = Open();
        try
        {
            Assert.True(LibC.TrySetWindowSize(replica, columns: 100, rows: 30));

            Assert.True(LibC.TryGetWindowSize(replica, out var columns, out var rows));
            Assert.Equal(100, columns);
            Assert.Equal(30, rows);
        }
        finally
        {
            _ = LibC.Close(replica);
            _ = LibC.Close(master);
        }
    }

    [Fact]
    public void The_master_forwards_the_size_it_is_given_to_the_replica()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The BSD pseudo-terminal is macOS only.");

        var (master, replica) = Open();
        try
        {
            // This is what lets the session resize the terminal without touching the replica's path again.
            Assert.True(LibC.TrySetWindowSize(master, columns: 120, rows: 40));

            Assert.True(LibC.TryGetWindowSize(replica, out var columns, out var rows));
            Assert.Equal(120, columns);
            Assert.Equal(40, rows);
        }
        finally
        {
            _ = LibC.Close(replica);
            _ = LibC.Close(master);
        }
    }

    [Fact]
    public void The_master_is_refused_while_no_replica_exists()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The BSD pseudo-terminal is macOS only.");

        var master = LibC.OpenPseudoTerminal(LibC.ORdWr | LibC.ONoCtty);
        Assert.True(master >= 0, $"posix_openpt failed (errno {Marshal.GetLastPInvokeError()}).");

        try
        {
            // The terminal only exists once a replica is open, so there is nothing to size yet. The host relies on
            // the shell's standard streams keeping a replica open for the whole session.
            Assert.False(LibC.TrySetWindowSize(master, columns: 100, rows: 30));
            Assert.Equal(25, Marshal.GetLastPInvokeError());
        }
        finally
        {
            _ = LibC.Close(master);
        }
    }

    /// <summary>Allocates a pseudo-terminal and returns both ends, with a replica already open.</summary>
    private static (int Master, int Replica) Open()
    {
        var master = LibC.OpenPseudoTerminal(LibC.ORdWr | LibC.ONoCtty);
        Assert.True(master >= 0, $"posix_openpt failed (errno {Marshal.GetLastPInvokeError()}).");
        Assert.Equal(0, LibC.GrantPseudoTerminal(master));
        Assert.Equal(0, LibC.UnlockPseudoTerminal(master));

        var buffer = new byte[256];
        int name;
        fixed (byte* value = buffer)
        {
            name = LibC.GetSlaveName(master, value, 256);
        }

        Assert.Equal(0, name);
        var length = Array.IndexOf(buffer, (byte)0);
        var path = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(buffer, 0, length) + "\0");
        int replica;
        fixed (byte* value = path)
        {
            replica = LibC.Open(value, LibC.ORdWr | LibC.ONoCtty);
        }

        Assert.True(replica >= 0, $"open() failed (errno {Marshal.GetLastPInvokeError()}).");
        return (master, replica);
    }
}
