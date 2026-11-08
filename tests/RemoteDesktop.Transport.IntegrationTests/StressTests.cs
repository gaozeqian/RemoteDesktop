using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Windows.MediaFoundation;
using RemoteDesktop.Platform.Windows.Terminal;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.File;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// Many sessions against one host at the same time, each streaming video, running commands and transferring files, and
/// sessions that come and go. This is the closest thing to the "does it hold up" check before an installer is shipped:
/// it looks for sessions, channels, shells or processes that outlive their session.
/// </summary>
public sealed class StressTests(ServerFixture server) : IClassFixture<ServerFixture>, IDisposable
{
    private const int Controllers = 4;
    private const int Reconnects = 10;

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Directory.CreateTempSubdirectory("rd-stress-").FullName;

    public void Dispose()
    {
        // _root is created per test instance, including when the Windows-only tests above are skipped.
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file still open after a failed session is cleaned up by the OS; teardown must not mask the failure.
        }
        catch (UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Several_sessions_stream_video_run_commands_and_transfer_files_at_once()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The features under load need Windows.");
        var codecs = CreateCodecs();
        await using var host = await StartHostAsync(codecs);
        var source = Path.Combine(_root, "payload.bin");
        File.WriteAllBytes(source, RandomBytes(4 * 1024 * 1024, seed: 11));
        var expected = File.ReadAllBytes(source);

        var sessions = new List<RemoteDesktop.Application.Sessions.RemoteSession>();
        var viewers = new List<DesktopViewer>();
        var terminals = new List<TerminalSession>();

        // The host numbers its sessions itself, so the channel checks below need its ids, not the controllers'.
        var hostSessionIds = new List<SessionId>();
        try
        {
            // All controllers connect first, so the host is serving them at the same time.
            for (var i = 0; i < Controllers; i++)
            {
                var controller = await TestDevice.StartAsync(server);
                
                var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
                sessions.Add(session);
                await WaitForAsync(() => host.HostSessions.ActiveSessions.Any(s => s.Connection.RemoteDevice == controller.Id), $"session {i} to appear on the host");
                hostSessionIds.Add(host.HostSessions.ActiveSessions.Single(s => s.Connection.RemoteDevice == controller.Id).Id);
                viewers.Add(await DesktopViewer.StartAsync(session.Context.Connection, codecs, 30, TimeProvider.System, NullLogger.Instance, Ct));
                terminals.Add(await TerminalSession.StartAsync(session.Context.Connection, "cmd", 80, 24, Ct));
            }

            Assert.Equal(Controllers, host.HostSessions.ActiveSessions.Count);
            foreach (var id in hostSessionIds)
            {
                await WaitForAsync(() => host.HostSessions.IsChannelOpen(id, ChannelType.Video), $"video channel for {id}");
                await WaitForAsync(() => host.HostSessions.IsChannelOpen(id, ChannelType.Terminal), $"terminal channel for {id}");
            }

            // Each of them gets pictures, runs its own shell and receives a file.
            var frames = viewers.Select(_ => 0).ToArray();
            for (var i = 0; i < viewers.Count; i++)
            {
                var index = i;
                viewers[i].FrameReady += (_, _) => Interlocked.Increment(ref frames[index]);
            }

            var transfers = new List<Task>();
            for (var i = 0; i < Controllers; i++)
            {
                var index = i;
                var folder = Path.Combine(_root, $"host{index}");
                Directory.CreateDirectory(folder);
                terminals[index].SendText($"echo session-{index}\r");
                transfers.Add(FileTransferClient.UploadAsync(
                    sessions[index].Context.Connection, source, folder, "payload.bin", ConflictPolicy.Overwrite, new SilentObserver(), Ct));
            }

            await Task.WhenAll(transfers).WaitAsync(Timeout, Ct);

            for (var i = 0; i < Controllers; i++)
            {
                Assert.Equal(expected, File.ReadAllBytes(Path.Combine(_root, $"host{i}", "payload.bin")));
                await WaitForAsync(() => terminals[i].Emulator.ScreenText().Contains($"session-{i}", StringComparison.Ordinal), $"session {i}'s command output");
            }

            foreach (var viewer in viewers)
            {
                var index = Array.IndexOf([.. viewers], viewer);
                await WaitForAsync(
                    () => viewer.Statistics.FramesPerSecond >= 5 && Volatile.Read(ref frames[index]) >= 5,
                    $"video frames on controller {index} to reach at least 5 fps");

                // Four streams at once must still be smooth: the host may not serialize them. The floor is well below
                // what the machine manages (about 30 fps each), so a slow machine does not fail the test.
                Assert.True(viewer.Statistics.FramesPerSecond >= 5, $"Only {viewer.Statistics.FramesPerSecond:0.0} fps with {Controllers} streams running.");
            }

            foreach (var (viewer, index) in viewers.Select((v, i) => (v, i)))
            {
                TestContext.Current.SendDiagnosticMessage($"controller {index}: {frames[index]} frames, {viewer.Statistics.FramesPerSecond:0} fps, {viewer.Statistics.Kbps / 1000:0.0} Mbps");
            }
        }
        finally
        {
            foreach (var terminal in terminals)
            {
                await terminal.DisposeAsync();
            }

            foreach (var viewer in viewers)
            {
                await viewer.DisposeAsync();
            }

            foreach (var session in sessions)
            {
                await session.DisposeAsync();
            }
        }

        // Nothing may outlive its session: no session, no channel, no shell.
        await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 0, "the host to forget every session");
        foreach (var id in hostSessionIds)
        {
            Assert.False(host.HostSessions.IsChannelOpen(id, ChannelType.Video));
            Assert.False(host.HostSessions.IsChannelOpen(id, ChannelType.Terminal));
        }
    }

    [Fact]
    public async Task Sessions_that_come_and_go_leave_nothing_behind()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The features under load need Windows.");
        var codecs = CreateCodecs();
        await using var host = await StartHostAsync(codecs);
        var shellsBefore = Process.GetProcessesByName("cmd").Length;

        for (var round = 0; round < Reconnects; round++)
        {
            await using var controller = await TestDevice.StartAsync(server);
            var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
            var viewer = await DesktopViewer.StartAsync(session.Context.Connection, codecs, 30, TimeProvider.System, NullLogger.Instance, Ct);
            await WaitForAsync(() => host.HostSessions.ActiveSessions.Any(s => s.Connection.RemoteDevice == controller.Id), $"session {round} to appear on the host");
            var hostId = host.HostSessions.ActiveSessions.Single(s => s.Connection.RemoteDevice == controller.Id).Id;
            var terminal = await TerminalSession.StartAsync(session.Context.Connection, "cmd", 80, 24, Ct);

            var frames = 0;
            viewer.FrameReady += (_, _) => Interlocked.Increment(ref frames);
            terminal.SendText("echo round\r");
            await WaitForAsync(() => frames >= 3, "a few frames");
            await WaitForAsync(() => terminal.Emulator.ScreenText().Contains("round", StringComparison.Ordinal), "the shell's answer");

            await terminal.DisposeAsync();
            await viewer.DisposeAsync();
            await session.DisposeAsync();

            // After every round the host must be back to nothing.
            await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 0, $"session {round} to be forgotten");
            await WaitForAsync(() => !host.HostSessions.IsChannelOpen(hostId, ChannelType.Terminal), "the terminal channel to close");
        }

        TestContext.Current.SendDiagnosticMessage($"{Reconnects} sessions came and went");

        // A leftover shell is already running when the last round ends, so the long session budget above would only
        // delay the report. The count is machine-wide: another program starting a cmd.exe of its own inflates it,
        // which is why the message says so rather than leaving the failure pointing at the terminal code.
        await WaitForAsync(
            () => Process.GetProcessesByName("cmd").Length <= shellsBefore,
            "the shells of finished sessions to be gone (the count is machine-wide, so an unrelated cmd.exe also trips this)",
            TimeSpan.FromSeconds(20));
    }

    private async Task<TestDevice> StartHostAsync(VideoCodecSelector codecs)
    {
        var files = Directory.CreateDirectory(Path.Combine(_root, "files")).FullName;
        var host = await TestDevice.StartAsync(
            server,
            forceRelay: false,
            new VideoChannelModule(new FakeDisplays(), new FakeCapturerFactory(), codecs, TimeProvider.System, NullLogger<VideoChannelModule>.Instance),
            new FileTransferModule(new AllowFiles(), new FileAccessPolicy([_root]), NullLogger<FileTransferModule>.Instance),
            new TerminalModule(new ConPtyHost(), new AllowFiles(), NullLogger<TerminalModule>.Instance));
        host.Credentials.AllowedPermissions = Permissions.All;
        _ = files;
        return host;
    }

    private static VideoCodecSelector CreateCodecs() =>
        new([new MediaFoundationH264EncoderFactory(hardware: false)], [new MediaFoundationH264DecoderFactory()]);

    private static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static async Task WaitForAsync(Func<bool> condition, string what, TimeSpan? budget = null)
    {
        var deadline = DateTime.UtcNow + (budget ?? Timeout);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(25, Ct);
        }
    }

    /// <summary>The tests run as an ordinary user, so files and shells may be served.</summary>
    private sealed class AllowFiles : IUserContextGuard
    {
        public string? GetRefusalReason() => null;
    }

    private sealed class SilentObserver : ITransferObserver
    {
        public void Started(long totalBytes, long startOffset, string finalPath, string partialPath)
        {
        }

        public void Progress(long bytes)
        {
        }
    }
}
