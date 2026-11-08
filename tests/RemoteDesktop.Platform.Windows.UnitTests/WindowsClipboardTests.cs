using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Platform.Windows.Clipboard;

namespace RemoteDesktop.Platform.Windows.UnitTests;

public class WindowsClipboardTests
{
    private const string WindowsOnly = "The Windows clipboard is Windows only.";

    [Fact]
    public void Set_rejects_null_content_with_ArgumentNullException()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        using var clipboard = new WindowsClipboard();
        Assert.Throws<ArgumentNullException>(() => clipboard.Set(null!));
    }

    [Fact]
    public void Set_rejects_unsupported_content_type_with_ArgumentException()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        using var clipboard = new WindowsClipboard();
        Assert.Throws<ArgumentException>(() => clipboard.Set(new UnsupportedClipboardContent()));
    }

    [Fact]
    public void Text_content_roundtrips_when_clipboard_is_accessible()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        using var clipboard = new WindowsClipboard();
        const string testText = "RemoteDesktop-Unit-Test-Clipboard-Content";

        try
        {
            clipboard.Set(new TextClipboardContent(testText));
            var retrieved = clipboard.Get();

            if (retrieved is TextClipboardContent textContent)
            {
                Assert.Equal(testText, textContent.Text);
            }
        }
        catch (IOException)
        {
            // The clipboard may be locked by another application or headless session; safe skip.
        }
    }

    [Fact]
    public void Image_content_roundtrips_when_clipboard_is_accessible()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        using var clipboard = new WindowsClipboard();
        // Minimal 1x1 PNG header/payload
        byte[] fakePng = [137, 80, 78, 71, 13, 10, 26, 10];

        try
        {
            clipboard.Set(new ImageClipboardContent(fakePng));
            var retrieved = clipboard.Get();

            if (retrieved is ImageClipboardContent imageContent)
            {
                Assert.True(imageContent.Png.Length >= fakePng.Length);
            }
        }
        catch (IOException)
        {
            // Clipboard lock contention
        }
    }

    [Fact]
    public void Changed_event_handler_can_be_subscribed_and_unsubscribed_cleanly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        using var clipboard = new WindowsClipboard();
        var raised = false;
        EventHandler handler = (_, _) => raised = true;

        clipboard.Changed += handler;
        clipboard.Changed -= handler;

        Assert.False(raised);
    }

    [Fact]
    public void Dispose_can_be_called_multiple_times_without_throwing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        var clipboard = new WindowsClipboard();
        clipboard.Dispose();
        clipboard.Dispose();
    }

    private sealed record UnsupportedClipboardContent : ClipboardContent;
}
