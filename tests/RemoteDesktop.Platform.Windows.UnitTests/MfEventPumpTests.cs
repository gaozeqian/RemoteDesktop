using System.Diagnostics;
using RemoteDesktop.Platform.Windows.MediaFoundation;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>
/// The event thread's shutdown order. The generator is a COM object whose only user is the pump thread; releasing it
/// while that thread is blocked inside <c>GetEvent</c> is a use-after-free, and a hung driver is exactly when it would
/// happen. The pump is expected to leak instead, until the thread is gone.
/// </summary>
public sealed class MfEventPumpTests
{
    [Fact]
    public void A_thread_stuck_in_GetEvent_keeps_its_generator()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");

        using var source = new BlockingEventSource();
        var pump = new MfEventPump(source);

        var clock = Stopwatch.StartNew();
        pump.Dispose();
        var waited = clock.Elapsed;

        Assert.True(
            waited >= TimeSpan.FromSeconds(1.5),
            $"Dispose returned after {waited}, before the shutdown budget had run out.");
        Assert.False(source.Disposed, "The generator was released while the pump thread was still inside GetEvent.");

        // Consumers are not left waiting for the wedged thread: the queue is completed before the join.
        Assert.False(pump.TryTake(out _, TimeSpan.FromMilliseconds(100)));

        // The driver eventually answers; a later Dispose can release what the first one deliberately left behind.
        source.Release();
        pump.Dispose();
        Assert.True(source.Disposed);
    }

    [Fact]
    public void A_pump_that_stopped_normally_releases_the_generator()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");

        using var source = new BlockingEventSource();
        var pump = new MfEventPump(source);

        // The caller shut the transform down first, which is what the contract asks; GetEvent then returns an error.
        source.Release();
        pump.Dispose();

        Assert.True(source.Disposed, "A pump whose thread has exited has to release the generator.");
    }

    /// <summary>Blocks in <c>GetEvent</c> until released, then fails the way a shut-down MFT does.</summary>
    private sealed class BlockingEventSource : IMfEventSource
    {
        private readonly ManualResetEventSlim _released = new();
        private int _disposed;

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public void Release() => _released.Set();

        public int GetEvent()
        {
            _released.Wait();
            throw new InvalidOperationException("The transform was shut down.");
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
}
