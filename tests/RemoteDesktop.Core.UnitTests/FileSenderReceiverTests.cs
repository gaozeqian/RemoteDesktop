using System.IO.Hashing;
using System.IO.Pipelines;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Protocol.File;
using RemoteDesktop.Protocol.Framing;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The two ends of one file's content on the wire, without a session around them: where the sender starts, what the
/// hash covers, what the receiver keeps of an interrupted attempt and what it refuses.
/// </summary>
public sealed class FileSenderReceiverTests : IDisposable
{
    private const int Chunk = FileChunkFrame.ChunkSize;

    private readonly string _folder = Directory.CreateTempSubdirectory("rd-filecore-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // ------------------------------------------------------------------ sender

    [Fact]
    public async Task The_sender_cuts_the_file_into_chunk_sized_frames_and_returns_the_hash_of_all_of_it()
    {
        var content = Content(Chunk * 2 + Chunk / 2);
        var path = Write("a.bin", content);
        var wire = new MemoryStream();
        var progress = new List<long>();

        var hash = await FileSender.SendAsync(wire, path, content.Length, 0, progress.Add, Ct);

        var frames = await ReadFramesAsync(wire);
        Assert.Equal([Chunk, Chunk, Chunk / 2], frames.Select(f => f.Payload.Length));
        Assert.All(frames, f => Assert.Equal(FileChunkFrame.MessageType, f.MessageType));
        Assert.Equal(content, frames.SelectMany(f => f.Payload.ToArray()).ToArray());
        Assert.Equal(XxHash64.HashToUInt64(content), hash);
        Assert.Equal([0, Chunk, Chunk * 2, content.Length], progress);
    }

    [Fact]
    public async Task A_start_offset_skips_the_bytes_the_receiver_has_but_the_hash_still_covers_the_whole_file()
    {
        var content = Content(Chunk + 1000);
        var path = Write("a.bin", content);
        var wire = new MemoryStream();
        var progress = new List<long>();

        var hash = await FileSender.SendAsync(wire, path, content.Length, 300, progress.Add, Ct);

        var frames = await ReadFramesAsync(wire);
        Assert.Equal(content[300..], frames.SelectMany(f => f.Payload.ToArray()).ToArray());
        Assert.Equal(XxHash64.HashToUInt64(content), hash);

        // Progress starts where the receiver already is, not at zero.
        Assert.Equal(300, progress[0]);
        Assert.Equal(content.Length, progress[^1]);
    }

    [Fact]
    public async Task A_start_offset_at_the_end_sends_no_frames_and_still_returns_the_hash()
    {
        var content = Content(5000);
        var path = Write("a.bin", content);
        var wire = new MemoryStream();

        var hash = await FileSender.SendAsync(wire, path, content.Length, content.Length, null, Ct);

        Assert.Equal(0, wire.Length);
        Assert.Equal(XxHash64.HashToUInt64(content), hash);
    }

    [Fact]
    public async Task An_empty_file_sends_nothing_and_hashes_to_the_empty_hash()
    {
        var path = Write("empty.bin", []);
        var wire = new MemoryStream();
        var progress = new List<long>();

        var hash = await FileSender.SendAsync(wire, path, 0, 0, progress.Add, Ct);

        Assert.Equal(0, wire.Length);
        Assert.Equal(XxHash64.HashToUInt64([]), hash);
        Assert.Equal([0L], progress);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task A_start_beyond_the_file_is_refused_before_anything_is_sent(long startOffset)
    {
        var path = Write("a.bin", Content(100));
        var wire = new MemoryStream();

        var failure = await Assert.ThrowsAsync<FileOperationException>(() => FileSender.SendAsync(wire, path, 100, startOffset, null, Ct));

        Assert.Equal(FileError.Changed, failure.Error);
        Assert.Equal(0, wire.Length);
    }

    [Fact]
    public async Task A_file_that_is_smaller_than_announced_is_reported_as_changed()
    {
        var path = Write("a.bin", Content(100));

        var failure = await Assert.ThrowsAsync<FileOperationException>(() => FileSender.SendAsync(new MemoryStream(), path, 200, 0, null, Ct));

        Assert.Equal(FileError.Changed, failure.Error);
    }

    [Fact]
    public async Task A_file_that_grew_is_sent_only_up_to_the_announced_size()
    {
        var content = Content(1000);
        var path = Write("a.bin", content);
        var wire = new MemoryStream();

        var hash = await FileSender.SendAsync(wire, path, 600, 0, null, Ct);

        var frames = await ReadFramesAsync(wire);
        Assert.Equal(content[..600], frames.SelectMany(f => f.Payload.ToArray()).ToArray());
        Assert.Equal(XxHash64.HashToUInt64(content.AsSpan(0, 600)), hash);
    }

    [Fact]
    public async Task A_file_that_does_not_exist_is_reported_as_not_found()
    {
        var failure = await Assert.ThrowsAsync<FileOperationException>(
            () => FileSender.SendAsync(new MemoryStream(), Path.Combine(_folder, "missing.bin"), 10, 0, null, Ct));

        Assert.Equal(FileError.NotFound, failure.Error);
        Assert.NotNull(failure.InnerException);
    }

    [Fact]
    public async Task A_file_that_another_program_has_open_for_writing_can_still_be_sent()
    {
        var content = Content(2000);
        var path = Write("busy.bin", content);
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        var wire = new MemoryStream();

        var hash = await FileSender.SendAsync(wire, path, content.Length, 0, null, Ct);

        Assert.Equal(XxHash64.HashToUInt64(content), hash);
    }

    [Fact]
    public async Task A_cancelled_send_throws_and_leaves_the_file_alone()
    {
        var content = Content(Chunk * 2);
        var path = Write("a.bin", content);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileSender.SendAsync(new MemoryStream(), path, content.Length, 0, null, cancelled.Token));

        Assert.Equal(content, await File.ReadAllBytesAsync(path, Ct));
    }

    // ------------------------------------------------------------------ receiver

    [Fact]
    public async Task The_receiver_writes_every_chunk_to_the_partial_file_and_returns_the_hash_of_all_of_it()
    {
        var content = Content(Chunk + 4321);
        var wire = await WireOfAsync(content, 0);
        var partial = Path.Combine(_folder, "in.rdpart");
        var progress = new List<long>();

        var hash = await FileReceiver.ReceiveAsync(wire, partial, content.Length, 0, progress.Add, Ct);

        Assert.Equal(content, await File.ReadAllBytesAsync(partial, Ct));
        Assert.Equal(XxHash64.HashToUInt64(content), hash);
        Assert.Equal([0, Chunk, content.Length], progress);
    }

    [Fact]
    public async Task A_resumed_receive_keeps_the_agreed_prefix_drops_what_follows_it_and_hashes_the_whole_file()
    {
        var content = Content(5000);
        var partial = Path.Combine(_folder, "in.rdpart");

        // 2000 bytes are agreed; the file on disk holds 3000, the last 1000 of which were never verified.
        await File.WriteAllBytesAsync(partial, [.. content[..2000], .. new byte[1000].Select(_ => (byte)0xEE)], Ct);
        var wire = await WireOfAsync(content, 2000);
        var progress = new List<long>();

        var hash = await FileReceiver.ReceiveAsync(wire, partial, content.Length, 2000, progress.Add, Ct);

        Assert.Equal(content, await File.ReadAllBytesAsync(partial, Ct));
        Assert.Equal(XxHash64.HashToUInt64(content), hash);
        Assert.Equal(2000, progress[0]);
    }

    [Fact]
    public async Task A_partial_file_shorter_than_the_agreed_start_is_reported_as_changed_and_left_as_it_was()
    {
        var partial = Path.Combine(_folder, "in.rdpart");
        await File.WriteAllBytesAsync(partial, Content(1000), Ct);

        var failure = await Assert.ThrowsAsync<FileOperationException>(
            () => FileReceiver.ReceiveAsync(new MemoryStream(), partial, 5000, 2000, null, Ct));

        Assert.Equal(FileError.Changed, failure.Error);
        Assert.Equal(1000, new FileInfo(partial).Length);
    }

    [Fact]
    public async Task Nothing_left_to_receive_reads_no_frames_and_returns_the_hash_of_what_is_there()
    {
        var content = Content(3000);
        var partial = Path.Combine(_folder, "in.rdpart");
        await File.WriteAllBytesAsync(partial, content, Ct);
        var wire = new MemoryStream([1, 2, 3]);

        var hash = await FileReceiver.ReceiveAsync(wire, partial, content.Length, content.Length, null, Ct);

        Assert.Equal(XxHash64.HashToUInt64(content), hash);
        Assert.Equal(0, wire.Position);
    }

    [Fact]
    public async Task A_sender_that_stops_early_leaves_what_arrived_in_the_partial_file_for_the_next_attempt()
    {
        var content = Content(Chunk * 2);
        var partial = Path.Combine(_folder, "in.rdpart");

        // One chunk of two, and then the stream ends.
        var wire = await WireOfAsync(content[..Chunk], 0);

        await Assert.ThrowsAsync<EndOfStreamException>(() => FileReceiver.ReceiveAsync(wire, partial, content.Length, 0, null, Ct));

        Assert.Equal(content[..Chunk], await File.ReadAllBytesAsync(partial, Ct));
    }

    [Fact]
    public async Task A_cancelled_receive_keeps_the_chunks_that_had_arrived()
    {
        var content = Content(Chunk * 2);
        var partial = Path.Combine(_folder, "in.rdpart");
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        await FrameCodec.WriteAsync(pipe.Writer.AsStream(), new Frame(FileChunkFrame.MessageType, content.AsMemory(0, Chunk)), Ct);
        using var cancel = new CancellationTokenSource();
        long received = 0;

        var receiving = FileReceiver.ReceiveAsync(
            pipe.Reader.AsStream(),
            partial,
            content.Length,
            0,
            bytes =>
            {
                received = bytes;
                if (bytes == Chunk)
                {
                    cancel.Cancel();
                }
            },
            cancel.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiving);
        Assert.Equal(Chunk, received);
        Assert.Equal(content[..Chunk], await File.ReadAllBytesAsync(partial, Ct));
    }

    [Fact]
    public async Task More_data_than_announced_is_a_protocol_error()
    {
        var content = Content(2000);
        var wire = await WireOfAsync(content, 0);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => FileReceiver.ReceiveAsync(wire, Path.Combine(_folder, "in.rdpart"), 1500, 0, null, Ct));
    }

    [Fact]
    public async Task An_empty_chunk_is_a_protocol_error_rather_than_a_way_to_stall_the_receiver()
    {
        var wire = new MemoryStream();
        await FrameCodec.WriteAsync(wire, new Frame(FileChunkFrame.MessageType, ReadOnlyMemory<byte>.Empty), Ct);
        wire.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => FileReceiver.ReceiveAsync(wire, Path.Combine(_folder, "in.rdpart"), 100, 0, null, Ct));
    }

    [Fact]
    public async Task A_frame_that_is_not_content_is_a_protocol_error_unless_the_caller_can_interpret_it()
    {
        var wire = new MemoryStream();
        await FrameCodec.WriteAsync(wire, new Frame(99, new byte[] { 1, 2, 3 }), Ct);
        var bytes = wire.ToArray();
        var partial = Path.Combine(_folder, "in.rdpart");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => FileReceiver.ReceiveAsync(new MemoryStream(bytes), partial, 100, 0, null, Ct));

        // Interpreting nothing is the same as not interpreting.
        await Assert.ThrowsAsync<InvalidDataException>(
            () => FileReceiver.ReceiveAsync(new MemoryStream(bytes), partial, 100, 0, null, Ct, interpretControl: _ => null));

        var reason = new FileOperationException(FileError.NotAllowed, "The sender gave up.");
        Frame? seen = null;
        var failure = await Assert.ThrowsAsync<FileOperationException>(
            () => FileReceiver.ReceiveAsync(
                new MemoryStream(bytes),
                partial,
                100,
                0,
                null,
                Ct,
                interpretControl: frame =>
                {
                    seen = frame;
                    return reason;
                }));

        Assert.Same(reason, failure);
        Assert.Equal(99, seen!.Value.MessageType);
    }

    [Fact]
    public async Task A_partial_file_in_a_folder_that_does_not_exist_is_an_io_error()
    {
        var failure = await Assert.ThrowsAsync<FileOperationException>(
            () => FileReceiver.ReceiveAsync(new MemoryStream(), Path.Combine(_folder, "nowhere", "in.rdpart"), 10, 0, null, Ct));

        Assert.Equal(FileError.Io, failure.Error);
        Assert.NotNull(failure.InnerException);
    }

    // ------------------------------------------------------------------ both

    [Fact]
    public async Task What_the_sender_sends_from_an_offset_the_receiver_completes_with_the_same_hash()
    {
        var content = Content(Chunk * 2 + 777);
        var path = Write("a.bin", content);
        var partial = Path.Combine(_folder, "in.rdpart");
        var halfway = Chunk + 5;
        await File.WriteAllBytesAsync(partial, content[..halfway], Ct);
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));

        var receiving = FileReceiver.ReceiveAsync(pipe.Reader.AsStream(), partial, content.Length, halfway, null, Ct);
        var sentHash = await FileSender.SendAsync(pipe.Writer.AsStream(), path, content.Length, halfway, null, Ct);
        var receivedHash = await receiving.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(sentHash, receivedHash);
        Assert.Equal(XxHash64.HashToUInt64(content), receivedHash);
        Assert.Equal(content, await File.ReadAllBytesAsync(partial, Ct));
    }

    private static byte[] Content(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static async Task<List<Frame>> ReadFramesAsync(MemoryStream wire)
    {
        wire.Position = 0;
        var frames = new List<Frame>();
        while (await FrameCodec.ReadAsync(wire, Chunk * 2, Ct) is { } frame)
        {
            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>What a sender would put on the wire for <paramref name="content"/> from <paramref name="startOffset"/>.</summary>
    private static async Task<MemoryStream> WireOfAsync(byte[] content, int startOffset)
    {
        var wire = new MemoryStream();
        for (var at = startOffset; at < content.Length; at += Chunk)
        {
            var length = Math.Min(Chunk, content.Length - at);
            await FrameCodec.WriteAsync(wire, new Frame(FileChunkFrame.MessageType, content.AsMemory(at, length)), Ct);
        }

        wire.Position = 0;
        return wire;
    }
}
