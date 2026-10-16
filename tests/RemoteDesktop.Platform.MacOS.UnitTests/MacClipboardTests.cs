using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Platform.MacOS.Clipboard;

namespace RemoteDesktop.Platform.MacOS.UnitTests;

/// <summary>
/// The pasteboard port, exercised for real.
/// </summary>
/// <remarks>
/// <para>
/// Every test here works on a pasteboard of its own rather than the general one. That is not tidiness: writing to
/// the general pasteboard would replace whatever the user had copied, and a suite that eats the clipboard is one
/// nobody runs twice — the symptom ("my copy vanished") does not look like a test at all.
/// </para>
/// <para>
/// What is <em>not</em> covered here is the "text wins over a picture" rule. Producing a pasteboard holding both at
/// once needs a native write of two types in one go, which the ABI deliberately does not offer — the managed side
/// always replaces the contents wholesale. The rule is implemented in <c>rd_clipboard.m</c> and is exercised by the
/// C probe that runs against the same library.
/// </para>
/// </remarks>
public class MacClipboardTests
{
    /// <summary>A 4x4 RGBA PNG: one red, green, blue and white row, repeated four times.</summary>
    private const string SamplePngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAAFUlEQVR42mP4z8DwHwwZ/oMBA+kCAA97J9n4xwmsAAAAAElFTkSuQmCC";

    /// <summary>
    /// A pasteboard shared by the tests in this class. Methods within one class run one after another, so there is
    /// no race on it; the tests that need to start empty make their own pasteboard instead.
    /// </summary>
    private const string SharedPasteboard = "com.remotedesktop.tests.clipboard";

    private static byte[] SamplePng() => Convert.FromBase64String(SamplePngBase64);

    /// <summary>A pasteboard nobody has written to, named uniquely so no other run can have touched it.</summary>
    private static string FreshPasteboard() => $"com.remotedesktop.tests.fresh.{Guid.NewGuid():N}";

    [Fact]
    public void Text_survives_a_round_trip()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The pasteboard port is macOS only.");

        using var clipboard = new MacClipboard(SharedPasteboard);
        clipboard.Set(new TextClipboardContent("hello pasteboard"));

        var content = Assert.IsType<TextClipboardContent>(clipboard.Get());
        Assert.Equal("hello pasteboard", content.Text);
    }

    [Fact]
    public void Text_that_is_not_ascii_survives_a_round_trip()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The pasteboard port is macOS only.");

        // This is the test for a specific mistake: the helper takes a *byte* length, and a managed string's Length
        // is a character count. Handing it the character count would make it read past the buffer, and the result
        // would be a truncated or corrupted string rather than a crash — which is exactly the kind of thing that
        // survives review. "中" is three bytes and one character; the emoji is four bytes and two.
        const string Text = "中文 émoji 🎉 — テスト";

        using var clipboard = new MacClipboard(SharedPasteboard);
        clipboard.Set(new TextClipboardContent(Text));

        var content = Assert.IsType<TextClipboardContent>(clipboard.Get());
        Assert.Equal(Text, content.Text);
    }

    [Fact]
    public void A_picture_survives_a_round_trip_byte_for_byte()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The pasteboard port is macOS only.");

        var png = SamplePng();
        using var clipboard = new MacClipboard(SharedPasteboard);
        clipboard.Set(new ImageClipboardContent(png));

        var content = Assert.IsType<ImageClipboardContent>(clipboard.Get());

        // Byte for byte, not "roughly the same picture": the protocol carries the PNG as-is, and re-encoding it
        // would change the hash the synchronizer uses to tell its own writes from the other side's.
        Assert.Equal(png, content.Png.ToArray());
    }

    [Fact]
    public void An_empty_pasteboard_reports_nothing_rather_than_failing()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The pasteboard port is macOS only.");

        using var clipboard = new MacClipboard(FreshPasteboard());

        // Empty is the ordinary state of a pasteboard, not an error: the helper reports it as success with a kind
        // of NONE, and the port turns that into null. Throwing here would make every session log a read failure the
        // first time the clipboard was cleared.
        Assert.Null(clipboard.Get());
    }

    [Fact]
    public void A_change_is_reported_to_a_listener()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The pasteboard port is macOS only.");

        using var clipboard = new MacClipboard(SharedPasteboard);
        using var signalled = new ManualResetEventSlim();

        clipboard.Changed += (_, _) => signalled.Set();
        clipboard.Set(new TextClipboardContent("something new"));

        // The port polls, because NSPasteboard has no notification for "somebody else took the clipboard". Ten
        // seconds is forty poll ticks, so a failure here is a real failure rather than a slow machine.
        Assert.True(
            signalled.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            "no change was reported within ten seconds.");
    }

    [Fact]
    public void Nothing_is_reported_after_the_last_listener_has_gone()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The pasteboard port is macOS only.");

        using var clipboard = new MacClipboard(SharedPasteboard);
        using var first = new ManualResetEventSlim();
        var seen = 0;

        void OnChanged(object? sender, EventArgs e)
        {
            Interlocked.Increment(ref seen);
            first.Set();
        }

        clipboard.Changed += OnChanged;
        clipboard.Set(new TextClipboardContent("while listening"));
        Assert.True(
            first.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            "the first change was never reported.");

        clipboard.Changed -= OnChanged;
        var before = Volatile.Read(ref seen);

        clipboard.Set(new TextClipboardContent("after listening stopped"));

        // Longer than several poll ticks. Polling only while somebody listens is the whole reason the timer is
        // reference-counted, and this is what would notice if the unsubscribe stopped stopping it.
        Thread.Sleep(TimeSpan.FromSeconds(1));
        Assert.Equal(before, Volatile.Read(ref seen));
    }

    [Fact]
    public void Two_pasteboards_do_not_see_each_others_content()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The pasteboard port is macOS only.");

        using var first = new MacClipboard(FreshPasteboard());
        using var second = new MacClipboard(FreshPasteboard());

        first.Set(new TextClipboardContent("only in the first"));

        var content = Assert.IsType<TextClipboardContent>(first.Get());
        Assert.Equal("only in the first", content.Text);
        Assert.Null(second.Get());
    }

    [Fact]
    public async Task Reads_from_different_pasteboards_do_not_corrupt_each_other()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The pasteboard port is macOS only.");

        // The helper keeps one read buffer for the whole process, and a read hands back a pointer into it that is
        // only valid until the next read — so two instances reading at the same time would overwrite each other's
        // bytes unless something serializes them. Each side reads a payload it can recognise, and a single
        // corrupted read fails the test.
        var textA = new string('a', 4096);
        var textB = new string('b', 4096);

        using var first = new MacClipboard(FreshPasteboard());
        using var second = new MacClipboard(FreshPasteboard());
        first.Set(new TextClipboardContent(textA));
        second.Set(new TextClipboardContent(textB));

        var cancellationToken = TestContext.Current.CancellationToken;
        var failures = 0;
        using var start = new ManualResetEventSlim();

        Task Read(MacClipboard clipboard, string expected) => Task.Run(
            () =>
            {
                start.Wait(cancellationToken);
                for (var i = 0; i < 200; i++)
                {
                    if (clipboard.Get() is not TextClipboardContent content || content.Text != expected)
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
            },
            cancellationToken);

        var reads = new[] { Read(first, textA), Read(second, textB) };
        start.Set();
        await Task.WhenAll(reads);

        Assert.Equal(0, failures);
    }

    [Fact]
    public void Content_this_platform_cannot_put_on_the_pasteboard_is_rejected()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The pasteboard port is macOS only.");

        using var clipboard = new MacClipboard(SharedPasteboard);

        // Silently doing nothing would leave the caller believing the clipboard had been updated.
        Assert.Throws<ArgumentException>(() => clipboard.Set(new UnsupportedContent()));
    }

    private sealed record UnsupportedContent : ClipboardContent;
}
