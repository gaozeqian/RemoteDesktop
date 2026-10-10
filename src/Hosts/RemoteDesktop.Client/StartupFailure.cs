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
internal static partial class UserMessage
{
    private const uint IconError = 0x00000010;

    public static void Show(string text, string caption)
    {
        if (OperatingSystem.IsWindows())
        {
            MessageBox(nint.Zero, text, caption, IconError);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint window, string text, string caption, uint type);
}
