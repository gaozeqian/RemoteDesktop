using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteDesktop.Client;

/// <summary>
/// The program could not start, and the reason is worth showing to whoever is looking at the screen.
/// </summary>
/// <remarks>
/// Separate from every other failure: this one happens before there is a window to put a message in, which is exactly
/// why the entry point catches it and puts one on the screen itself.
/// </remarks>
internal sealed class StartupFailure(string reason, string reportPath) : Exception(reason)
{
    /// <summary>Where the same sentence was written down, for whoever is asked to look into it.</summary>
    public string ReportPath { get; } = reportPath;
}

/// <summary>A message box from the system, for the failures that happen before there is a window to use.</summary>
/// <remarks>
/// Windows goes straight to <c>MessageBoxW</c>. macOS has no equivalent one-liner in a process that has not started its
/// UI toolkit yet — and it must not, because the failure may well be that the toolkit cannot start. So the alert is
/// shown by <c>osascript</c>, which is part of the base system: the caption and text are handed over as arguments to
/// an AppleScript <c>run</c> handler rather than spliced into the script source, so nothing in a path or a connection
/// string can be read as AppleScript.
/// </remarks>
internal static partial class UserMessage
{
    private const uint IconError = 0x00000010;

    public static void Show(string text, string caption)
    {
        if (OperatingSystem.IsWindows())
        {
            MessageBox(nint.Zero, text, caption, IconError);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            ShowWithAppleScript(text, caption);
        }
    }

    private static void ShowWithAppleScript(string text, string caption)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("osascript")
            {
                // `--` ends osascript's own options: everything after it reaches `on run argv`, which is why the
                // message needs no escaping.
                ArgumentList =
                {
                    "-e", "on run argv",
                    "-e", "display alert (item 1 of argv) message (item 2 of argv) as critical",
                    "-e", "end run",
                    "--", caption, text,
                },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });

            // An alert waits for the person in front of the screen, so this waits with it. Bounded all the same: a
            // dialog nobody answers must not turn a startup failure into a process that never exits.
            process?.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // Showing the reason is a courtesy; failing to show it must not replace the reason with a second failure.
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint window, string text, string caption, uint type);
}
