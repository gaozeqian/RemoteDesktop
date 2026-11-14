using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Platform.Linux.Clipboard;
using RemoteDesktop.Platform.Linux.XdgPortal;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// The real portal clipboard has to report a copy made by another application.
/// </summary>
/// <remarks>
/// <para>
/// This is the regression test for the two-machine acceptance failure: on GNOME the portal does not send
/// <c>SelectionOwnerChanged</c> when an unrelated program takes the selection, so a clipboard that only listens for
/// that signal never reads the new content. The fake clipboard in <c>SharingSessionTests</c> raises the event itself
/// and cannot catch this; only the real portal and a real second clipboard owner can.
/// </para>
/// <para>
/// Opt-in, like the other portal tests: opening a control session asks the person at the machine for permission.
/// It needs <c>wl-copy</c> (the <c>wl-clipboard</c> package), which is the second owner the test copies with. The
/// test leaves the clipboard cleared afterwards.
/// </para>
/// </remarks>
[Collection(DesktopEnvironmentGroup.Name)]
public sealed class WaylandClipboardChangeTests(ITestOutputHelper output)
{
    private const string LinuxOnly = "The desktop portal and wl-clipboard, which only exist on Linux.";
    private const string NeedsConsent = "Needs a person to allow control and wl-copy; set RD_PORTAL_TEST=1 to run it.";

    [Fact]
    public async Task An_external_copy_is_reported_when_the_portal_announces_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        Assert.SkipUnless(Environment.GetEnvironmentVariable("RD_PORTAL_TEST") == "1", NeedsConsent);

        var wlCopy = Find("wl-copy");
        Assert.SkipWhen(wlCopy is null, "wl-copy is not installed; install wl-clipboard to run this test.");

        output.WriteLine(
            $"WAYLAND_DISPLAY={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")} "
            + $"XDG_RUNTIME_DIR={Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")} "
            + $"DBUS_SESSION_BUS_ADDRESS={Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")}");

        var cancellationToken = TestContext.Current.CancellationToken;
        using var control = new PortalControlSession(
            NullLoggerFactory.Instance,
            NullLogger<PortalControlSession>.Instance);
        using var clipboard = new WaylandClipboard(control, NullLogger<WaylandClipboard>.Instance);

        var granted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        control.AccessStatusChanged += (_, status) =>
        {
            if (status.State == InputAccessState.Granted)
            {
                granted.TrySetResult();
            }
        };

        // The portal can report the selection that was already there when the session opened. That is deliberately
        // not what a session sends, and it must not satisfy this test either; only the unique text copied below does.
        var expected = $"rd-clipboard-change-{Guid.NewGuid():N}";
        var changed = new TaskCompletionSource<ClipboardContent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        clipboard.Changed += (_, _) =>
        {
            try
            {
                var content = clipboard.Get();
                if (content is TextClipboardContent text && text.Text == expected)
                {
                    output.WriteLine("Changed fired with the copied text.");
                    changed.TrySetResult(content);
                }
            }
            catch (Exception error)
            {
                changed.TrySetException(error);
            }
        };

        // The subscription above opens the control session and starts watching.
        await granted.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
        output.WriteLine("Control was granted.");

        // Wait for the first poll instead of sleeping: it records the selection that was already there without
        // reporting it. Copying before it finishes can make that first read see the new text and prime on it.
        await clipboard.Primed.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        output.WriteLine("The initial clipboard was recorded.");

        output.WriteLine($"Copying '{expected}' with {wlCopy}.");
        await RunWlCopyAsync(wlCopy!, expected, cancellationToken);
        output.WriteLine("wl-copy finished.");

        var content = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        Assert.NotNull(content);
        Assert.Equal(expected, Assert.IsType<TextClipboardContent>(content).Text);

        await ClearAsync(wlCopy!, cancellationToken);
    }

    private static string? Find(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(directory, executable);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static async Task RunWlCopyAsync(string executable, string text, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        Assert.True(process.ExitCode == 0, $"{executable} exited {process.ExitCode}: {error}");
    }

    private static async Task ClearAsync(string executable, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            ArgumentList = { "--clear" },
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        Assert.True(process.ExitCode == 0, $"{executable} --clear exited {process.ExitCode}: {error}");
    }
}
