namespace RemoteDesktop.Platform.Windows.UnitTests;

public class WindowsKeepAwakeTests
{
    private const string WindowsOnly = "SetThreadExecutionState is Windows only.";

    [Fact]
    public void Acquire_and_Release_manages_execution_state_lifecycle()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        var keepAwake = new WindowsKeepAwake();
        keepAwake.Acquire();
        keepAwake.Release();
    }

    [Fact]
    public void Nested_Acquire_and_Release_increments_and_decrements_cleanly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        var keepAwake = new WindowsKeepAwake();
        keepAwake.Acquire();
        keepAwake.Acquire();
        keepAwake.Acquire();

        keepAwake.Release();
        keepAwake.Release();
        keepAwake.Release();
    }

    [Fact]
    public void Excessive_Release_resets_safely_without_throwing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        var keepAwake = new WindowsKeepAwake();
        keepAwake.Acquire();

        keepAwake.Release();
        keepAwake.Release(); // Second release when count is already 0
        keepAwake.Release(); // Third release
    }
}
