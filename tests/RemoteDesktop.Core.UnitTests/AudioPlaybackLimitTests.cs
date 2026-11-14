using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The audio channel is the one stream whose payloads are small by construction (Opus packets), so the receiver has
/// an explicit 64 KiB frame limit. A host that asks for a 16 MiB buffer per "packet" must be refused before the
/// allocation, not played until memory runs out.
/// </summary>
public sealed class AudioPlaybackLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_frame_above_the_limit_ends_the_stream_without_creating_a_player()
    {
        var (server, client) = await TestPipe.ConnectAsync(Ct);
        await using var serverEnd = server;
        await using var clientEnd = client;

        var players = new CountingPlayerFactory();
        await using var playback = await AudioPlayback.StartAsync(
            new FakeConnection(serverEnd), players, NullLogger.Instance, Ct);

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        playback.Closed += (_, _) => closed.TrySetResult();

        // The header alone is enough: FrameCodec checks the length before it reads or allocates the payload. One byte
        // above the receiver's limit is the smallest input that proves the limit is enforced.
        var header = new byte[FrameCodec.HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, 64 * 1024 + 1);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(sizeof(uint)), 0);
        await clientEnd.WriteAsync(header, Ct);
        await clientEnd.FlushAsync(Ct);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(0, players.Created);
    }

    private sealed class CountingPlayerFactory : IAudioPlayerFactory
    {
        private int _created;

        public int Created => Volatile.Read(ref _created);

        public IAudioPlayer Create()
        {
            Interlocked.Increment(ref _created);
            return new SilentPlayer();
        }
    }

    private sealed class SilentPlayer : IAudioPlayer
    {
        public TimeSpan Buffered => TimeSpan.Zero;

        public Task StartAsync(AudioFormat format) => Task.CompletedTask;

        public void Enqueue(ReadOnlySpan<float> samples)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
