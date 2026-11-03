using RemoteDesktop.Application.Links;
using RemoteDesktop.Client;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>
/// Opening a second client for a <c>remotedesktop://</c> link would register the same controller identity at the
/// rendezvous server, which keeps one session per device — the two processes would evict each other every second. The
/// link is therefore handed to the running instance over a pipe and the second process exits.
/// </summary>
public sealed class LinkActivationTests
{
    // A pipe of its own: the machine running the tests may have a real client holding the production one. The name is
    // kept short because on macOS the pipe is a Unix socket under a long per-user temp directory, and a socket path
    // may not exceed 104 characters.
    private static string FreshPipeName() => "rd.link.test." + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task A_second_launch_hands_the_link_to_the_running_client()
    {
        var pipe = FreshPipeName();
        var received = new TaskCompletionSource<ConnectionLink>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var listener = LinkActivation.TryListen(link => received.TrySetResult(link), pipe);
        Assert.NotNull(listener);

        Assert.True(ConnectionLink.TryParse("remotedesktop://connect?code=902226105", out var link));
        Assert.True(LinkActivation.TryForward(link, pipe));

        var taken = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("902226105", taken.Device.ToString());
    }

    [Fact]
    public void Only_the_first_client_takes_the_pipe()
    {
        var pipe = FreshPipeName();

        using var owner = LinkActivation.TryListen(_ => { }, pipe);
        Assert.NotNull(owner);

        // The second window still opens; it just never receives links, so the first one keeps them.
        Assert.Null(LinkActivation.TryListen(_ => { }, pipe));
    }

    [Fact]
    public void Forwarding_without_a_running_client_reports_failure_instead_of_throwing()
    {
        Assert.True(ConnectionLink.TryParse("remotedesktop://902226105", out var link));

        // Nothing listens on this name: the caller must start normally rather than crash or hang.
        Assert.False(LinkActivation.TryForward(link, FreshPipeName()));
    }

    /// <summary>
    /// The link is not a configuration argument: both the host builder and Avalonia refuse one they do not recognise,
    /// which would leave the person with a program that does not start.
    /// </summary>
    [Fact]
    public void The_link_is_taken_out_of_the_arguments_the_rest_of_startup_sees()
    {
        var (link, rest) = Program.SplitLink(["remotedesktop://connect?code=902226105", "--DataDirectory", @"D:\rd2"]);

        Assert.NotNull(link);
        Assert.Equal("902226105", link.Value.Device.ToString());
        Assert.Equal(["--DataDirectory", @"D:\rd2"], rest);
    }

    [Fact]
    public void A_list_of_arguments_without_a_link_is_left_alone()
    {
        var (link, rest) = Program.SplitLink(["--Transport:ForceRelay", "true"]);

        Assert.Null(link);
        Assert.Equal(["--Transport:ForceRelay", "true"], rest);
    }

    /// <summary>A malformed link is swallowed rather than passed on, so the program still starts.</summary>
    [Fact]
    public void A_malformed_link_is_dropped_and_the_window_still_opens()
    {
        var (link, rest) = Program.SplitLink(["remotedesktop://connect", "--DataDirectory", "C:\\data"]);

        Assert.Null(link);
        Assert.Equal(["--DataDirectory", "C:\\data"], rest);
    }
}
