using System.IO.Pipes;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Protocol.Clipboard;
using RemoteDesktop.Protocol.Framing;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>Two clipboards kept in step over a real pipe, each played by a fake that behaves like the OS clipboard.</summary>
public class ClipboardSynchronizerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    [Fact]
    public async Task A_copy_on_one_side_appears_on_the_other_and_does_not_bounce_back()
    {
        await using var rig = await Rig.StartAsync();

        rig.A.Copy(new TextClipboardContent("hello 你好"));
        await rig.B.WaitForAsync(c => c is TextClipboardContent { Text: "hello 你好" });

        // The pipe is ordered, so once a later copy has arrived an echo of the first one would already have been set.
        rig.A.Copy(new TextClipboardContent("sentinel"));
        await rig.B.WaitForAsync(c => c is TextClipboardContent { Text: "sentinel" });

        Assert.Equal(0, rig.A.SetCalls);
        Assert.Equal(2, rig.B.SetCalls);
    }

    [Fact]
    public async Task Pictures_are_shared_too_and_travel_in_both_directions()
    {
        await using var rig = await Rig.StartAsync();

        rig.B.Copy(new ImageClipboardContent(Png));
        await rig.A.WaitForAsync(c => c is ImageClipboardContent image && image.Png.Span.SequenceEqual(Png));

        rig.A.Copy(new TextClipboardContent("and back"));
        await rig.B.WaitForAsync(c => c is TextClipboardContent { Text: "and back" });
    }

    [Fact]
    public async Task What_was_on_the_clipboard_before_the_session_is_never_sent()
    {
        var a = new FakeClipboard();
        var b = new FakeClipboard();
        a.Copy(new TextClipboardContent("my private note"));
        await using var rig = await Rig.StartAsync(a, b);

        // A later copy goes through; if the pre-session note had been sent, it would have been set first.
        a.Copy(new TextClipboardContent("after the session started"));
        await b.WaitForAsync(c => c is TextClipboardContent { Text: "after the session started" });

        Assert.Equal(1, b.SetCalls);
    }

    [Fact]
    public async Task Oversized_text_is_not_sent()
    {
        await using var rig = await Rig.StartAsync();

        rig.A.Copy(new TextClipboardContent(new string('x', (4 * 1024 * 1024) + 1)));
        rig.A.Copy(new TextClipboardContent("small"));

        // Ordered: had the oversized text been sent, it would have been set before "small".
        await rig.B.WaitForAsync(c => c is TextClipboardContent { Text: "small" });
        Assert.Equal(1, rig.B.SetCalls);
    }

    [Fact]
    public async Task Content_that_claims_to_be_a_picture_but_is_not_one_is_rejected()
    {
        var a = new FakeClipboard();
        var b = new FakeClipboard();
        var (first, second) = await PipePair.CreateAsync();
        var run = new ClipboardSynchronizer(b, NullLogger<ClipboardSynchronizer>.Instance).RunAsync(second, Ct);

        var fake = new ClipboardMessage { Image = new ClipboardImage { Png = ByteString.CopyFromUtf8("<script>not a png</script>") } };
        await ProtobufFrames.WriteAsync(first, fake, Ct);
        await ProtobufFrames.WriteAsync(first, new ClipboardMessage { Text = new ClipboardText { Text = "after" } }, Ct);

        await b.WaitForAsync(c => c is TextClipboardContent { Text: "after" });
        Assert.Equal(1, b.SetCalls); // only the text was applied
        await first.DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _ = a;
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly Task _runA;
        private readonly Task _runB;

        private Rig(FakeClipboard a, FakeClipboard b, Task runA, Task runB, CancellationTokenSource stop)
        {
            A = a;
            B = b;
            _runA = runA;
            _runB = runB;
            _stop = stop;
        }

        public FakeClipboard A { get; }

        public FakeClipboard B { get; }

        public static async Task<Rig> StartAsync(FakeClipboard? a = null, FakeClipboard? b = null)
        {
            a ??= new FakeClipboard();
            b ??= new FakeClipboard();
            var (left, right) = await PipePair.CreateAsync();
            var stop = new CancellationTokenSource();
            var runA = new ClipboardSynchronizer(a, NullLogger<ClipboardSynchronizer>.Instance).RunAsync(left, stop.Token);
            var runB = new ClipboardSynchronizer(b, NullLogger<ClipboardSynchronizer>.Instance).RunAsync(right, stop.Token);
            return new Rig(a, b, runA, runB, stop);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await Task.WhenAll(_runA, _runB).WaitAsync(TimeSpan.FromSeconds(10));
            _stop.Dispose();
        }
    }

    /// <summary>Like the OS clipboard: setting it (by anyone) raises Changed.</summary>
    private sealed class FakeClipboard : IClipboard
    {
        private readonly Lock _lock = new();
        private ClipboardContent? _content;

        public event EventHandler? Changed;

        public int SetCalls { get; private set; }

        public ClipboardContent? Get()
        {
            lock (_lock)
            {
                return _content;
            }
        }

        public void Set(ClipboardContent content)
        {
            lock (_lock)
            {
                _content = content;
                SetCalls++;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The user copies something: the clipboard changes without anyone calling Set.</summary>
        public void Copy(ClipboardContent content)
        {
            lock (_lock)
            {
                _content = content;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        public async Task WaitForAsync(Func<ClipboardContent?, bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!condition(Get()))
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the clipboard.");
                await Task.Delay(30, Ct);
            }
        }
    }

    private static class PipePair
    {
        public static async Task<(Stream First, Stream Second)> CreateAsync()
        {
            var name = $"rd-clip-{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());
            return (server, client);
        }
    }
}
