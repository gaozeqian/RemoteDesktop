using System.Diagnostics;
using RemoteDesktop.Platform.Linux.Setup;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// The process runner behind the "restart the controlled end" housekeeping. What matters is that a child which fills
/// both pipes, or never exits, cannot hang the caller.
/// </summary>
public sealed class LinuxSetupTests
{
    private const string LinuxOnly = "Runs /bin/sh.";

    private static ProcessStartInfo Shell(string script)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        return startInfo;
    }

    [Fact]
    public async Task Output_larger_than_a_pipe_on_both_streams_does_not_block_the_child()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // Stderr is written first and far exceeds the pipe buffer (64 KiB): a reader that drains stdout to the end
        // before touching stderr would leave the child stuck in its stderr write forever.
        var script = "head -c 1000000 /dev/zero >&2; head -c 1000000 /dev/zero";

        var finished = Task.Run(() => LinuxSetup.RunToCompletion(Shell(script), TimeSpan.FromSeconds(30)), TestContext.Current.CancellationToken);

        // The timeout inside the runner would also end a hang, but as "false"; asserting "true" is what proves it drained.
        Assert.True(await finished);
    }

    [Fact]
    public void A_child_that_outlives_the_timeout_is_killed_and_reported()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var stopwatch = Stopwatch.StartNew();
        var completed = LinuxSetup.RunToCompletion(Shell("sleep 60"), TimeSpan.FromMilliseconds(500));
        stopwatch.Stop();

        Assert.False(completed);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"Took {stopwatch.Elapsed}.");
    }
}
