using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.MacOS.Video;
using RemoteDesktop.Platform.Windows.Camera;
using RemoteDesktop.Platform.Windows.MediaFoundation;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Camera;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// The camera over real sessions: synthetic pictures through the real encoder, QUIC and decoder, the host's consent
/// controls, and one test with the real camera that only measures.
/// </summary>
public sealed class CameraSessionTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static readonly CameraMode Vga = new(640, 480, 30);
    private static readonly CameraMode Small = new(320, 240, 15);
    private static readonly CameraMode Wide = new(1280, 720, 30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(640, 480, 30, 640, 480, 30)]
    [InlineData(1920, 1080, 30, 1280, 720, 30)] // nearest size
    [InlineData(640, 480, 10, 640, 480, 30)] // only one rate at that size
    [InlineData(300, 200, 60, 320, 240, 15)]
    [InlineData(0, 0, 0, 320, 240, 15)]
    public void The_closest_mode_the_camera_offers_is_used(int width, int height, int frameRate, int expectedWidth, int expectedHeight, int expectedRate)
    {
        var chosen = CameraModule.ChooseMode([Vga, Small, Wide], width, height, frameRate);

        Assert.Equal(new CameraMode(expectedWidth, expectedHeight, expectedRate), chosen);
    }

    [Fact]
    public void A_camera_without_modes_has_nothing_to_choose() => Assert.Null(CameraModule.ChooseMode([], 640, 480, 30));

    [Fact]
    public async Task The_camera_is_available_by_default_and_can_be_disabled_by_policy()
    {
        // Available by default
        await using var defaultPair = await Pair.StartAsync(server, allowCamera: true);
        Assert.True(defaultPair.Session.Context.Grant.Allows(Permissions.Camera));
        Assert.NotEmpty(await CameraViewer.ListCamerasAsync(defaultPair.Session.Context.Connection, Ct));

        // When disallowed by policy, the session cannot use it
        await using var pair = await Pair.StartAsync(server, allowCamera: false);
        Assert.False(pair.Session.Context.Grant.Allows(Permissions.Camera));
        await Assert.ThrowsAsync<CameraException>(() => CameraViewer.ListCamerasAsync(pair.Session.Context.Connection, Ct));

        // Allowed later, a new session gets it.
        pair.Host.Credentials.AllowedPermissions |= Permissions.Camera;
        await using var next = await pair.Controller.Controller.ConnectAsync(pair.Host.Id, pair.Host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
        Assert.True(next.Context.Grant.Allows(Permissions.Camera));
        Assert.NotEmpty(await CameraViewer.ListCamerasAsync(next.Context.Connection, Ct));
    }

    [Fact]
    public async Task Watch_mode_never_gets_the_camera_even_when_the_host_allows_it()
    {
        await using var pair = await Pair.StartAsync(server, allowCamera: true, mode: SessionMode.View);

        Assert.False(pair.Session.Context.Grant.Allows(Permissions.Camera));
        await Assert.ThrowsAsync<CameraException>(() => CameraViewer.ListCamerasAsync(pair.Session.Context.Connection, Ct));
    }

    [Fact]
    public async Task A_host_that_was_not_granted_the_camera_says_so_instead_of_listing_nothing()
    {
        // "No cameras" and "the desktop has not allowed the camera" are different facts, and only the second one can
        // be fixed by the person at the host with one click. The host answers the list request with a refusal and a
        // sentence, which the controller shows instead of its own generic empty-list message.
        foreach (var state in new[] { CameraAccessState.Unset, CameraAccessState.Denied })
        {
            await using var pair = await Pair.StartAsync(server, allowCamera: true, cameraAccess: new FakeCameraAccess(state));

            var refused = await Assert.ThrowsAsync<CameraException>(() => CameraViewer.ListCamerasAsync(pair.Session.Context.Connection, Ct));
            Assert.Equal(CameraError.NotAllowed, refused.Error);
            Assert.Contains("摄像头权限", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_host_lists_its_cameras_with_their_modes()
    {
        await using var pair = await Pair.StartAsync(server, allowCamera: true);

        var cameras = await CameraViewer.ListCamerasAsync(pair.Session.Context.Connection, Ct);

        Assert.Equal(["Front camera", "Back camera"], cameras.Select(c => c.Name));
        Assert.Equal([Wide, Vga, Small], cameras[0].Modes);
        Assert.Equal("front", cameras[0].Id);
    }

    [Fact]
    public async Task The_picture_arrives_decoded_in_the_requested_size()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation codecs are Windows only.");
        var codecs = CreateCodecs();
        await using var pair = await Pair.StartAsync(server, allowCamera: true);

        var frames = 0;
        await using var viewer = await CameraViewer.StartAsync(pair.Session.Context.Connection, codecs, "front", Vga, TimeProvider.System, NullLogger.Instance, Ct);
        viewer.FrameReady += (_, _) => Interlocked.Increment(ref frames);
        await WaitUntilAsync(() => Volatile.Read(ref frames) >= 10);

        Assert.Equal("Front camera", viewer.Details!.CameraName);
        Assert.Equal((640, 480, 30), (viewer.Details.Width, viewer.Details.Height, viewer.Details.FrameRate));
        var bgra = new byte[640 * 480 * 4];
        Assert.True(viewer.CopyLatestFrame(bgra, 640 * 4, 640, 480) > 0);

        // The synthetic picture is red on the left, blue on the right (BGRA order).
        var left = ((240 * 640) + 160) * 4;
        var right = ((240 * 640) + 480) * 4;
        Assert.InRange(bgra[left + 2], 170, 255);
        Assert.InRange(bgra[right + 0], 170, 255);
        Assert.InRange(bgra[right + 2], 0, 90);
    }

    [Fact]
    public async Task A_BGRA_camera_is_converted_instead_of_being_dropped()
    {
        // AVFoundation delivers BGRA; the Windows and Linux sources deliver NV12. The module used to skip anything
        // that was not NV12, so on macOS the camera opened, reported a picture size, and then never produced a
        // frame — the host log said "camera opened" and nothing else. This test would have timed out there.
        Assert.SkipUnless(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
            "This test needs the H.264 codecs of one of these platforms.");
        var codecs = CreateCodecs();
        await using var pair = await Pair.StartAsync(server, allowCamera: true);
        pair.Source.Format = PixelFormat.Bgra32;

        var frames = 0;
        await using var viewer = await CameraViewer.StartAsync(pair.Session.Context.Connection, codecs, "front", Vga, TimeProvider.System, NullLogger.Instance, Ct);
        viewer.FrameReady += (_, _) => Interlocked.Increment(ref frames);
        await WaitUntilAsync(() => Volatile.Read(ref frames) >= 10);

        Assert.Equal((640, 480), (viewer.Details!.Width, viewer.Details.Height));
        var bgra = new byte[640 * 480 * 4];
        Assert.True(viewer.CopyLatestFrame(bgra, 640 * 4, 640, 480) > 0);

        // The synthetic picture is red on the left and blue on the right, in BGRA byte order.
        var left = ((240 * 640) + 160) * 4;
        var right = ((240 * 640) + 480) * 4;
        Assert.InRange(bgra[left + 2], 170, 255);
        Assert.InRange(bgra[right + 0], 170, 255);
        Assert.InRange(bgra[right + 2], 0, 90);
    }

    [Fact]
    public async Task The_camera_is_in_use_only_while_the_viewer_is_looking_and_released_when_it_leaves()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation codecs are Windows only.");
        await using var pair = await Pair.StartAsync(server, allowCamera: true);
        // The host registers the session a moment after the handshake, so wait for it rather than assume it is there.
        await WaitUntilAsync(() => pair.Host.HostSessions.ActiveSessions.Count == 1);
        var hostId = pair.Host.HostSessions.ActiveSessions.Single().Id;
        Assert.False(pair.Host.HostSessions.IsChannelOpen(hostId, ChannelType.Camera));

        var viewer = await CameraViewer.StartAsync(pair.Session.Context.Connection, CreateCodecs(), "front", Vga, TimeProvider.System, NullLogger.Instance, Ct);
        await WaitUntilAsync(() => pair.Source.Capturing);
        Assert.True(pair.Host.HostSessions.IsChannelOpen(hostId, ChannelType.Camera));

        await viewer.DisposeAsync();

        await WaitUntilAsync(() => !pair.Source.Capturing);
        await WaitUntilAsync(() => !pair.Host.HostSessions.IsChannelOpen(hostId, ChannelType.Camera));
    }

    [Fact]
    public async Task A_camera_that_cannot_be_opened_or_is_unknown_is_refused_with_a_reason()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation codecs are Windows only.");
        await using var pair = await Pair.StartAsync(server, allowCamera: true);
        var connection = pair.Session.Context.Connection;

        var unknown = await Assert.ThrowsAsync<CameraException>(() =>
            CameraViewer.StartAsync(connection, CreateCodecs(), "no-such-camera", Vga, TimeProvider.System, NullLogger.Instance, Ct));
        Assert.Equal(CameraError.NotFound, unknown.Error);

        pair.Source.FailToStart = "Another program is using the camera.";
        var busy = await Assert.ThrowsAsync<CameraException>(() =>
            CameraViewer.StartAsync(connection, CreateCodecs(), "front", Vga, TimeProvider.System, NullLogger.Instance, Ct));
        Assert.Equal(CameraError.Unavailable, busy.Error);
        Assert.Contains("Another program", busy.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_camera_that_disappears_mid_stream_ends_the_stream_with_the_reason()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation codecs are Windows only.");
        await using var pair = await Pair.StartAsync(server, allowCamera: true);
        pair.Source.FailAfterFrames = 15;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var viewer = await CameraViewer.StartAsync(pair.Session.Context.Connection, CreateCodecs(), "front", Vga, TimeProvider.System, NullLogger.Instance, Ct);
        viewer.Closed += (_, _) => closed.TrySetResult();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Contains("unplugged", viewer.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_streamed_while_the_host_can_only_act_as_the_system()
    {
        var guard = new SwitchableGuard { Reason = "Not on the login screen." };
        await using var pair = await Pair.StartAsync(server, allowCamera: true, guard: guard);

        var refused = await Assert.ThrowsAsync<CameraException>(() => CameraViewer.ListCamerasAsync(pair.Session.Context.Connection, Ct));

        Assert.Equal(CameraError.NotAllowed, refused.Error);
    }

    [Fact]
    public async Task The_host_can_stop_the_camera_of_one_session_and_the_controller_follows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation codecs are Windows only.");
        await using var pair = await Pair.StartAsync(server, allowCamera: true);
        var control = pair.HostControl();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Session.GrantChanged += (_, _) => changed.TrySetResult();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewer = await CameraViewer.StartAsync(pair.Session.Context.Connection, CreateCodecs(), "front", Vga, TimeProvider.System, NullLogger.Instance, Ct);
        viewer.Closed += (_, _) => closed.TrySetResult();

        var before = (await control.GetStatusAsync(Ct)).Sessions.Single();
        Assert.True(before.CameraActive);
        await control.StopCameraAsync(before.Id, Ct);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.False(pair.Session.Context.Grant.Allows(Permissions.Camera));
        Assert.True(pair.Session.Context.Grant.Allows(Permissions.ViewScreen)); // the session itself goes on
        await WaitUntilAsync(() => !pair.Source.Capturing);
        await WaitUntilAsync(() => !control.GetStatusAsync(Ct).GetAwaiter().GetResult().Sessions.Single().CameraActive);
    }

    [Fact]
    public async Task A_real_camera_streams_through_the_whole_path_and_only_numbers_are_checked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");
        var enumerator = new MediaFoundationCameraEnumerator();
        var camera = enumerator.GetCameras().FirstOrDefault(c => c.Modes.Count > 0);
        Assert.SkipWhen(camera is null, "No camera is connected.");
        var mode = camera.Modes.FirstOrDefault(m => m.Width == 1280 && m.Height == 720 && m.FrameRate >= 15) ?? camera.Modes[^1];
        var codecs = CreateCodecs();

        var module = new CameraModule(enumerator, new MediaFoundationCameraSourceFactory(enumerator), new SwitchableGuard(), codecs, TimeProvider.System, NullLogger<CameraModule>.Instance);
        await using var host = await TestDevice.StartAsync(server, forceRelay: false, module);
        host.Credentials.AllowedPermissions = Permissions.All;
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);

        var listed = await CameraViewer.ListCamerasAsync(session.Context.Connection, Ct);
        Assert.Equal(camera.Name, listed[0].Name);

        var frames = 0;
        var clock = new Stopwatch();
        await using var viewer = await CameraViewer.StartAsync(session.Context.Connection, codecs, camera.Id, mode, TimeProvider.System, NullLogger.Instance, Ct);
        viewer.FrameReady += (_, _) =>
        {
            if (Interlocked.Increment(ref frames) == 1)
            {
                clock.Start();
            }
        };
        await WaitUntilAsync(() => Volatile.Read(ref frames) >= 60);
        clock.Stop();

        TestContext.Current.SendDiagnosticMessage($"{viewer.Details!.CameraName} {viewer.Details.Width}x{viewer.Details.Height}@{viewer.Details.FrameRate} via {viewer.Details.EncoderName}: {(frames - 1) / clock.Elapsed.TotalSeconds:0.0} fps, {viewer.Statistics.Kbps:0} kbps, latency {viewer.Statistics.LatencyMilliseconds:0} ms");
        Assert.Equal((mode.Width, mode.Height), (viewer.Details.Width, viewer.Details.Height));
        var bgra = new byte[mode.Width * mode.Height * 4];
        Assert.True(viewer.CopyLatestFrame(bgra, mode.Width * 4, mode.Width, mode.Height) > 0);
        // What the camera sees is not ours to judge (a dark room decodes to almost nothing); the colours of a known picture are
        // checked with the synthetic camera above. Only report how varied this one was.
        TestContext.Current.SendDiagnosticMessage($"decoded picture: {bgra.Where((_, i) => i % 4001 == 0).Distinct().Count()} distinct sampled values");
    }

    /// <summary>
    /// The H.264 codecs of the platform this runs on. Most camera tests are Windows-only for other reasons (Media
    /// Foundation sources, installer details); the synthetic-camera path is not, and macOS is the platform whose
    /// camera source delivers BGRA, so it has to be able to run there.
    /// </summary>
    private static VideoCodecSelector CreateCodecs() =>
        OperatingSystem.IsMacOS()
            ? new([new VideoToolboxH264EncoderFactory(hardware: false)], [new VideoToolboxH264DecoderFactory()])
            : new([new MediaFoundationH264EncoderFactory(hardware: false)], [new MediaFoundationH264DecoderFactory()]);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(25, Ct);
        }
    }

    private sealed class FakeCameraAccess(CameraAccessState state) : ICameraAccess
    {
        public Task<CameraAccessState> GetStateAsync(CancellationToken cancellationToken) => Task.FromResult(state);

        public Task SetAllowedAsync(bool allowed, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The test only reads the state.");
    }

    private sealed class SwitchableGuard : IUserContextGuard
    {
        public volatile string? Reason;

        public string? GetRefusalReason() => Reason;
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _secrets = [];

        public void Save(string name, ReadOnlySpan<byte> secret) => _secrets[name] = secret.ToArray();

        public byte[]? Load(string name) => _secrets.GetValueOrDefault(name);

        public void Delete(string name) => _secrets.TryRemove(name, out _);
    }

    /// <summary>Two cameras; the first delivers a synthetic picture, left half red and right half blue, with a moving bar.</summary>
    private sealed class FakeCameras(FakeSource source) : ICameraEnumerator, ICameraSourceFactory
    {
        public IReadOnlyList<CameraInfo> GetCameras() =>
        [
            new("front", "Front camera", [Wide, Vga, Small]),
            new("back", "Back camera", [Small]),
        ];

        public ICameraSource Open(string cameraId)
        {
            if (source.FailToStart is { } reason)
            {
                throw new CameraUnavailableException(reason);
            }

            return source;
        }
    }

    private sealed class FakeSource : ICameraSource
    {
        private int _capturing;

        public CameraInfo Camera => new("front", "Front camera", [Vga]);

        public bool Capturing => Volatile.Read(ref _capturing) != 0;

        public string? FailToStart { get; set; }

        public int FailAfterFrames { get; set; } = int.MaxValue;

        /// <summary>What this camera delivers. AVFoundation delivers BGRA; the Windows and Linux sources NV12.</summary>
        public PixelFormat Format { get; set; } = PixelFormat.Nv12;

        public async IAsyncEnumerable<VideoFrame> CaptureAsync(CameraMode mode, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Volatile.Write(ref _capturing, 1);
            try
            {
                for (var index = 0; ; index++)
                {
                    if (index >= FailAfterFrames)
                    {
                        throw new CameraUnavailableException("The camera was unplugged.");
                    }

                    await Task.Delay(1000 / mode.FrameRate, cancellationToken).ConfigureAwait(false);
                    yield return Picture(mode.Width, mode.Height, index);
                }
            }
            finally
            {
                Volatile.Write(ref _capturing, 0);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private MemoryVideoFrame Picture(int width, int height, int index)
        {
            var frame = new MemoryVideoFrame(width, height, Format, TimeSpan.FromMilliseconds(index * 33));
            var buffer = frame.Buffer;
            var bar = (index * 8) % width;

            if (Format == PixelFormat.Bgra32)
            {
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var left = x < width / 2;
                        var highlight = y < 20 && Math.Abs(x - bar) < 10;
                        var p = (y * frame.Stride) + (x * 4);
                        buffer[p] = (byte)(highlight ? 235 : left ? 0 : 255); // B
                        buffer[p + 1] = (byte)(highlight ? 235 : 0); // G
                        buffer[p + 2] = (byte)(highlight ? 235 : left ? 255 : 0); // R
                        buffer[p + 3] = 255; // A
                    }
                }

                return frame;
            }

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var left = x < width / 2;
                    buffer[(y * frame.Stride) + x] = (byte)(y < 20 && Math.Abs(x - bar) < 10 ? 235 : left ? 81 : 41);
                }
            }

            // Red (U low, V high) on the left, blue (U high, V low) on the right.
            for (var y = 0; y < height / 2; y++)
            {
                for (var x = 0; x < width; x += 2)
                {
                    var p = (height * frame.Stride) + (y * frame.Stride) + x;
                    var left = x < width / 2;
                    buffer[p] = (byte)(left ? 90 : 240);
                    buffer[p + 1] = (byte)(left ? 240 : 110);
                }
            }

            return frame;
        }
    }

    private sealed class Pair : IAsyncDisposable
    {
        public TestDevice Host { get; private set; } = null!;

        public TestDevice Controller { get; private set; } = null!;

        public RemoteSession Session { get; private set; } = null!;

        public FakeSource Source { get; } = new();

        public static async Task<Pair> StartAsync(
            ServerFixture server,
            bool allowCamera,
            SessionMode mode = SessionMode.Control,
            IUserContextGuard? guard = null,
            ICameraAccess? cameraAccess = null)
        {
            var pair = new Pair();
            var cameras = new FakeCameras(pair.Source);
            var module = new CameraModule(cameras, cameras, guard ?? new SwitchableGuard(), CreateCodecs(), TimeProvider.System, NullLogger<CameraModule>.Instance, cameraAccess);
            pair.Host = await TestDevice.StartAsync(server, forceRelay: false, module);
            if (!allowCamera)
            {
                pair.Host.Credentials.AllowedPermissions &= ~Permissions.Camera;
            }

            pair.Controller = await TestDevice.StartAsync(server);
            pair.Session = await pair.Controller.Controller.ConnectAsync(pair.Host.Id, pair.Host.Credentials.TemporaryPassword, mode, Ct);
            return pair;
        }

        public LocalHostControl HostControl() => new(Host.Rendezvous, Host.Credentials, Host.HostSessions, null, false);

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await Controller.DisposeAsync();
            await Host.DisposeAsync();
        }
    }
}
