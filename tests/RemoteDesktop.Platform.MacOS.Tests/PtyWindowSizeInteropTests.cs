using System.Runtime.InteropServices;
using RemoteDesktop.Platform.MacOS.Interop;

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
/// have caught it.
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
            Assert.True(LibSystem.TrySetWindowSize(replica, columns: 100, rows: 30));

            Assert.True(LibSystem.TryGetWindowSize(replica, out var columns, out var rows));
            Assert.Equal(100, columns);
            Assert.Equal(30, rows);
        }
        finally
        {
            _ = LibSystem.close(replica);
            _ = LibSystem.close(master);
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
            Assert.True(LibSystem.TrySetWindowSize(master, columns: 120, rows: 40));

            Assert.True(LibSystem.TryGetWindowSize(replica, out var columns, out var rows));
            Assert.Equal(120, columns);
            Assert.Equal(40, rows);
        }
        finally
        {
            _ = LibSystem.close(replica);
            _ = LibSystem.close(master);
        }
    }

    [Fact]
    public void The_master_is_refused_while_no_replica_exists()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The BSD pseudo-terminal is macOS only.");

        var master = LibSystem.posix_openpt(LibSystem.ORdWr | LibSystem.ONoCtty);
        Assert.True(master >= 0, $"posix_openpt failed (errno {Marshal.GetLastPInvokeError()}).");

        try
        {
            // The terminal only exists once a replica is open, so there is nothing to size yet. The host relies on
            // the shell's standard streams keeping a replica open for the whole session.
            Assert.False(LibSystem.TrySetWindowSize(master, columns: 100, rows: 30));
            Assert.Equal(25, Marshal.GetLastPInvokeError());
        }
        finally
        {
            _ = LibSystem.close(master);
        }
    }

    /// <summary>Allocates a pseudo-terminal and returns both ends, with a replica already open.</summary>
    private static (int Master, int Replica) Open()
    {
        var master = LibSystem.posix_openpt(LibSystem.ORdWr | LibSystem.ONoCtty);
        Assert.True(master >= 0, $"posix_openpt failed (errno {Marshal.GetLastPInvokeError()}).");
        Assert.Equal(0, LibSystem.grantpt(master));
        Assert.Equal(0, LibSystem.unlockpt(master));

        var name = LibSystem.ToManagedString(LibSystem.ptsname(master));
        Assert.NotNull(name);

        var path = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        int replica;
        fixed (byte* value = path)
        {
            replica = LibSystem.open(value, LibSystem.ORdWr | LibSystem.ONoCtty, 0);
        }

        Assert.True(replica >= 0, $"open('{name}') failed (errno {Marshal.GetLastPInvokeError()}).");
        return (master, replica);
    }
}
