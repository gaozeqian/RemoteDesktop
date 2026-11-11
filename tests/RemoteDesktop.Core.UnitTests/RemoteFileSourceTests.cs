using RemoteDesktop.Domain;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.File;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The file pane asks the other computer one question at a time and waits for the answer. A host that accepted the
/// channel and then said nothing left it waiting for as long as the session lasted.
/// </summary>
public sealed class RemoteFileSourceTests
{
    [Fact]
    public async Task A_host_that_never_answers_ends_the_request_instead_of_hanging()
    {
        var source = new RemoteFileSource(new SilentConnection(), TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<FileOperationException>(() => source.ListAsync("/home", TestContext.Current.CancellationToken));

        Assert.Equal(FileError.Io, ex.Error);
        Assert.Contains("did not answer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disposing_the_source_while_a_request_is_in_flight_does_not_turn_its_failure_into_an_object_disposed_error()
    {
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new RemoteFileSource(new GatedConnection(gate), TimeSpan.FromSeconds(30));

        var pending = source.ListAsync("/home", TestContext.Current.CancellationToken);
        await source.DisposeAsync();
        gate.SetException(new IOException("The channel was closed."));

        // The request still ends the way a lost connection always does, rather than failing on the released lock.
        var ex = await Assert.ThrowsAsync<FileOperationException>(() => pending);
        Assert.Equal(FileError.Io, ex.Error);
    }

    [Fact]
    public async Task A_read_only_request_on_a_reused_channel_that_broke_is_repeated_on_a_fresh_channel()
    {
        var connection = new ScriptedConnection(
            [ListingFrame("/a")],
            [ListingFrame("/b")]);
        var source = new RemoteFileSource(connection, TimeSpan.FromSeconds(30));

        await source.ListAsync("/a", TestContext.Current.CancellationToken);
        var contents = await source.ListAsync("/b", TestContext.Current.CancellationToken);

        Assert.Equal("/b", contents.Path);
        Assert.Equal(2, connection.Channels.Count);
    }

    [Fact]
    public async Task A_changing_request_on_a_reused_channel_that_broke_is_not_repeated_because_the_host_may_have_done_it()
    {
        var connection = new ScriptedConnection(
            [ListingFrame("/a")],
            [ResultFrame()]);
        var source = new RemoteFileSource(connection, TimeSpan.FromSeconds(30));
        await source.ListAsync("/a", TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<FileOperationException>(() => source.DeleteAsync("/a/file", recursive: false, TestContext.Current.CancellationToken));

        Assert.Equal(FileError.Io, ex.Error);
        Assert.Contains("may already have taken effect", ex.Message, StringComparison.Ordinal);
        Assert.Single(connection.Channels);
        Assert.Equal(1, connection.TotalRequests(FileRequest.BodyOneofCase.Delete));
    }

    [Fact]
    public async Task A_changing_request_on_a_brand_new_channel_keeps_failing_with_the_plain_connection_error()
    {
        var connection = new ScriptedConnection([]);
        var source = new RemoteFileSource(connection, TimeSpan.FromSeconds(30));

        var ex = await Assert.ThrowsAsync<FileOperationException>(() => source.DeleteAsync("/a/file", recursive: false, TestContext.Current.CancellationToken));

        Assert.Equal(FileError.Io, ex.Error);
        Assert.Equal("The connection to the remote computer failed.", ex.Message);
        Assert.Equal(1, connection.TotalRequests(FileRequest.BodyOneofCase.Delete));
    }

    private static byte[] ListingFrame(string path) => Frame(new FileResponse { Listing = new DirectoryListing { Path = path, Status = new FileStatus() } });

    private static byte[] ResultFrame() => Frame(new FileResponse { Result = new OperationResult { Status = new FileStatus() } });

    private static byte[] Frame(FileResponse response)
    {
        using var buffer = new MemoryStream();
        ProtobufFrames.WriteAsync(buffer, response).AsTask().GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    /// <summary>
    /// Hands out one scripted channel per script; each channel answers with its canned frames and then fails every
    /// further read the way a broken connection does. A channel opened after the scripts run out answers nothing.
    /// </summary>
    private sealed class ScriptedConnection(params byte[][][] scripts) : IPeerConnection
    {
        private int _next;

        public List<ScriptedChannel> Channels { get; } = [];

        public DeviceId RemoteDevice => default;

        public ConnectionPath Path => ConnectionPath.Direct;

        public string? RelaySessionId => null;

        public string LocalFingerprint => "local";

        public string RemoteFingerprint => "remote";

        public int TotalRequests(FileRequest.BodyOneofCase kind) => Channels.Sum(c => c.Requests.Count(r => r.BodyCase == kind));

        public ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken)
        {
            var channel = new ScriptedChannel(type, _next < scripts.Length ? scripts[_next] : []);
            _next++;
            Channels.Add(channel);
            return ValueTask.FromResult<IChannel>(channel);
        }

        public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedChannel(ChannelType type, byte[][] frames) : IChannel
    {
        private readonly ScriptedStream _stream = new(frames);

        public ChannelType Type => type;

        public Stream Stream => _stream;

        public IReadOnlyList<FileRequest> Requests => _stream.Requests;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Records each request frame written; reads return the canned answers, then throw <see cref="IOException"/>.</summary>
    private sealed class ScriptedStream(byte[][] frames) : Stream
    {
        private readonly Queue<byte[]> _pending = new(frames);
        private readonly List<byte> _written = [];
        private byte[] _current = [];
        private int _offset;

        public List<FileRequest> Requests { get; } = [];

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // A frame arrives as a header write followed by a payload write: collect until one is complete.
            _written.AddRange(buffer.ToArray());
            const int headerSize = 6;
            while (_written.Count >= headerSize)
            {
                var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(_written.ToArray());
                if (_written.Count < headerSize + length)
                {
                    return;
                }

                using var frame = new MemoryStream(_written.GetRange(0, headerSize + length).ToArray());
                _written.RemoveRange(0, headerSize + length);
                var request = await ProtobufFrames.ReadAsync(frame, FileRequest.Parser, cancellationToken: cancellationToken);
                if (request is not null)
                {
                    Requests.Add(request);
                }
            }
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _current.Length)
            {
                if (!_pending.TryDequeue(out var next))
                {
                    throw new IOException("The connection was reset.");
                }

                _current = next;
                _offset = 0;
            }

            var n = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            return ValueTask.FromResult(n);
        }
    }

    private sealed class SilentConnection : IPeerConnection
    {
        public DeviceId RemoteDevice => default;

        public ConnectionPath Path => ConnectionPath.Direct;

        public string? RelaySessionId => null;

        public string LocalFingerprint => "local";

        public string RemoteFingerprint => "remote";

        public ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel>(new SilentChannel(type));

        public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SilentChannel(ChannelType type) : IChannel
    {
        public ChannelType Type => type;

        public Stream Stream { get; } = new SilentStream();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Takes whatever is written and never answers a read: a host that accepted the channel and went quiet.</summary>
    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class GatedConnection(TaskCompletionSource<int> gate) : IPeerConnection
    {
        public DeviceId RemoteDevice => default;

        public ConnectionPath Path => ConnectionPath.Direct;

        public string? RelaySessionId => null;

        public string LocalFingerprint => "local";

        public string RemoteFingerprint => "remote";

        public ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel>(new GatedChannel(type, new GatedStream(gate)));

        public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GatedChannel(ChannelType type, Stream stream) : IChannel
    {
        public ChannelType Type => type;

        public Stream Stream => stream;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Takes whatever is written; a read ends only when the test completes the gate.</summary>
    private sealed class GatedStream(TaskCompletionSource<int> gate) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            await gate.Task.WaitAsync(cancellationToken);
    }
}
