namespace RemoteDesktop.Platform.Windows.UnitTests;

public class WindowsKeepAwakeTests
{
    private const string WindowsOnly = "SetThreadExecutionState is Windows only.";

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsAwayModeRequired = 0x00000040;

    [Fact]
    public void Acquire_and_Release_manages_execution_state_lifecycle()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        using var keepAwake = new WindowsKeepAwake();
        keepAwake.Acquire();
        keepAwake.Release();
    }

    [Fact]
    public void Nested_Acquire_and_Release_increments_and_decrements_cleanly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        using var keepAwake = new WindowsKeepAwake();
        var calls = new List<uint>();
        keepAwake.ExecutionStateForTests = flags =>
        {
            calls.Add(flags);
            return 1;
        };

        keepAwake.Acquire();
        keepAwake.Acquire();
        keepAwake.Acquire();

        // The execution state is process-visible only while at least one request is held, so only the
        // transition 0 -> 1 and 1 -> 0 touches Win32.
        Assert.Equal(new[] { EsContinuous | EsSystemRequired | EsAwayModeRequired }, calls);

        keepAwake.Release();
        keepAwake.Release();
        Assert.Single(calls);

        keepAwake.Release();
        Assert.Equal(new[] { EsContinuous | EsSystemRequired | EsAwayModeRequired, EsContinuous }, calls);
    }

    [Fact]
    public async Task Acquire_and_Release_run_on_the_same_dedicated_thread()
    {
        // SetThreadExecutionState is thread-affine. Session code acquires before an await and releases in a
        // finally, so the two calls normally land on different pool threads; both must still reach the one
        // thread that owns the execution state.
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        using var keepAwake = new WindowsKeepAwake();
        var calls = new List<(int Thread, uint Flags)>();
        keepAwake.ExecutionStateForTests = flags =>
        {
            lock (calls)
            {
                calls.Add((Environment.CurrentManagedThreadId, flags));
            }

            return 1;
        };

        keepAwake.Acquire();
        await Task.Run(keepAwake.Release, TestContext.Current.CancellationToken);

        Assert.Equal(2, calls.Count);
        Assert.Equal(calls[0].Thread, calls[1].Thread);
        Assert.Equal(EsContinuous | EsSystemRequired | EsAwayModeRequired, calls[0].Flags);
        Assert.Equal(EsContinuous, calls[1].Flags);
    }

    [Fact]
    public void Excessive_Release_resets_safely_without_throwing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        using var keepAwake = new WindowsKeepAwake();
        keepAwake.Acquire();

        keepAwake.Release();
        keepAwake.Release(); // Second release when count is already 0
        keepAwake.Release(); // Third release
    }
}
